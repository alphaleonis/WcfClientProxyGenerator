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

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      public ClassDeclarationSyntax GenerateProxyClass(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol sourceProxyInterface, string name = null, Accessibility accessibility = Accessibility.Public)
      {
         if (name == null)
         {
            if (sourceProxyInterface.Name.StartsWith("I"))
               name = sourceProxyInterface.Name.Substring(1) + "Proxy";
            else
               name = sourceProxyInterface.Name + "Proxy";
         }

         // Resolve the callback contract if any
         ITypeSymbol serviceContractAttributeType = RequireTypeSymbol(context, "System.ServiceModel.ServiceContractAttribute");
         AttributeData serviceContractAttribute = sourceProxyInterface.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Equals(serviceContractAttributeType));
         if (serviceContractAttribute == null)
            throw new TextFileGeneratorException(sourceProxyInterface, $"The interface {sourceProxyInterface.Name} is not decorated with ServiceContractAttribute.");

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
            baseType = RequireTypeSymbol(context, "System.ServiceModel.DuplexClientBase`1").Construct(sourceProxyInterface);
         }
         else
         {
            baseType = RequireTypeSymbol(context, "System.ServiceModel.ClientBase`1").Construct(sourceProxyInterface);
         }

         // Create class declaration
         SyntaxNode targetClass = context.Generator.ClassDeclaration(name, accessibility: accessibility, baseType: context.Generator.TypeExpression(baseType), interfaceTypes: new[] { context.Generator.TypeExpression(sourceProxyInterface) });

         // Copy constructors from base class.
         foreach (var baseCtor in baseType.Constructors.Where(ctor => ctor.DeclaredAccessibility != Accessibility.Private))
         {
            var targetCtor = context.Generator.ConstructorDeclaration(baseCtor, baseCtor.Parameters.Select(p => context.Generator.Argument(context.Generator.IdentifierName(p.Name))));
            targetCtor = context.Generator.WithAccessibility(targetCtor, Accessibility.Public);
            targetClass = context.Generator.AddMembers(targetClass, targetCtor).AddNewLineTrivia().AddNewLineTrivia();
         }

         foreach (IMethodSymbol sourceMethod in GetOperationContractMethods(context, sourceProxyInterface))
         {
            SyntaxNode targetMethod = context.Generator.MethodDeclaration(sourceMethod);
            targetMethod = context.Generator.WithModifiers(targetMethod, DeclarationModifiers.None);

            bool isVoid = sourceMethod.ReturnType.SpecialType == SpecialType.System_Void;
            targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();

            var expression = context.Generator.InvocationExpression(
               context.Generator.MemberAccessExpression(
                  context.Generator.MemberAccessExpression(
                     context.Generator.BaseExpression(),
                     "Channel"
                  ),
                  sourceMethod.Name
               ),
               sourceMethod.Parameters.Select(p => context.Generator.IdentifierName(p.Name)).ToArray()
            );

            SyntaxNode statement;
            if (!isVoid)
               statement = context.Generator.ReturnStatement(expression);
            else
               statement = context.Generator.ExpressionStatement(expression);

            targetMethod = context.Generator.WithStatements(targetMethod,
               new[]
               {
                  statement
               }
            );
            targetClass = context.Generator.AddMembers(targetClass, targetMethod);
         }

         return (ClassDeclarationSyntax)targetClass;
      }
   }   
}
