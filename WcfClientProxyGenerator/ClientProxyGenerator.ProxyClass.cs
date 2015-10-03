using Alphaleonis.Vsx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Editing;
using System.CodeDom.Compiler;
using System.Reflection;
using AlphaVSX.Roslyn;
using Microsoft.CodeAnalysis.CSharp;
using System.Threading;
using System.Runtime.InteropServices;
using Alphaleonis.Vsx.Roslyn;
using Alphaleonis.Vsx.Roslyn.CSharp;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      public ClassDeclarationSyntax GenerateProxyClass(SemanticModel semanticModel, SyntaxGenerator generator, INamedTypeSymbol sourceProxyInterface, string name, Accessibility accessibility, bool suppressWarningComments, MemberAccessibility constructorAccessibility, out IEnumerable<IMethodSymbol> sourceConstructors)
      {
         if (name == null)
         {
            if (sourceProxyInterface.Name.StartsWith("I"))
               name = sourceProxyInterface.Name.Substring(1) + "Proxy";
            else
               name = sourceProxyInterface.Name + "Proxy";
         }

         var compilation = semanticModel.Compilation;

         // Resolve the callback contract if any
         ITypeSymbol serviceContractAttributeType = compilation.RequireTypeByMetadataName("System.ServiceModel.ServiceContractAttribute");
         AttributeData serviceContractAttribute = sourceProxyInterface.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Equals(serviceContractAttributeType));
         if (serviceContractAttribute == null)
            throw new CodeGeneratorException(sourceProxyInterface, $"The interface {sourceProxyInterface.Name} is not decorated with ServiceContractAttribute.");

         ITypeSymbol callbackContractType;
         var callbackContractArg = serviceContractAttribute.NamedArguments.FirstOrDefault(arg => arg.Key.Equals("CallbackContract"));
         if (callbackContractArg.Key != null)
            callbackContractType = callbackContractArg.Value.Value as ITypeSymbol;
         else
            callbackContractType = null;

         // Resolve the base type (ClientBase or DuplexClientBase depending on whether a CallbackContract exists or not)
         INamedTypeSymbol baseType;
         if (callbackContractType != null)
         {
            baseType = compilation.RequireTypeByMetadataName("System.ServiceModel.DuplexClientBase`1").Construct(sourceProxyInterface);
         }
         else
         {
            baseType = compilation.RequireTypeByMetadataName("System.ServiceModel.ClientBase`1").Construct(sourceProxyInterface);
         }

         // Create class declaration
         SyntaxNode targetClass = generator.ClassDeclaration(name, accessibility: accessibility, baseType: generator.TypeExpression(baseType), interfaceTypes: new[] { generator.TypeExpression(sourceProxyInterface) });

         targetClass = generator.AddWarningCommentIf(!suppressWarningComments, targetClass);


         // Copy constructors from base class.
         sourceConstructors = baseType.Constructors.Where(ctor => ctor.DeclaredAccessibility != Accessibility.Private).ToImmutableArray();

         foreach (var baseCtor in sourceConstructors)
         {
            var targetCtor = generator.ConstructorDeclaration(baseCtor, baseCtor.Parameters.Select(p => generator.Argument(generator.IdentifierName(p.Name))));

            targetCtor = generator.AddWarningCommentIf(!suppressWarningComments, targetCtor);

            targetCtor = generator.WithAccessibility(targetCtor, ToAccessibility(constructorAccessibility));
            targetClass = generator.AddMembers(targetClass, targetCtor.AddNewLineTrivia());
         }

         foreach (IMethodSymbol sourceMethod in GetOperationContractMethods(semanticModel.Compilation, sourceProxyInterface))
         {
            SyntaxNode targetMethod = generator.MethodDeclaration(sourceMethod);

            targetMethod = generator.AddWarningCommentIf(!suppressWarningComments, targetMethod);

            targetMethod = generator.WithModifiers(targetMethod, DeclarationModifiers.None);

            bool isVoid = sourceMethod.ReturnType.SpecialType == SpecialType.System_Void;
            targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();

            var expression = generator.InvocationExpression(
               generator.MemberAccessExpression(
                  generator.MemberAccessExpression(
                     generator.BaseExpression(),
                     "Channel"
                  ),
                  sourceMethod.Name
               ),
               sourceMethod.Parameters.Select(p => generator.IdentifierName(p.Name)).ToArray()
            );

            SyntaxNode statement;
            if (!isVoid)
               statement = generator.ReturnStatement(expression);
            else
               statement = generator.ExpressionStatement(expression);

            targetMethod = generator.WithStatements(targetMethod,
               new[]
               {
                  statement
               }
            );
            targetClass = generator.AddMembers(targetClass, targetMethod.AddNewLineTrivia());
         }

         return (ClassDeclarationSyntax)targetClass;
      }

      private static Accessibility ToAccessibility(MemberAccessibility accessibility)
      {
         switch (accessibility)
         {
            case MemberAccessibility.Public:
               return Accessibility.Public;

            case MemberAccessibility.Protected:
               return Accessibility.Protected;

            case MemberAccessibility.Internal:
               return Accessibility.Internal;

            case MemberAccessibility.Private:
               return Accessibility.Private;

            case MemberAccessibility.ProtectedInternal:
               return Accessibility.ProtectedOrInternal;

            default:
               throw new NotSupportedException($"Invalid accessibility {accessibility}.");
         }
      }
   }
}
