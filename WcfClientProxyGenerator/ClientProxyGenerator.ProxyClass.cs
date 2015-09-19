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
      public ClassDeclarationSyntax GenerateProxyClass(INamedTypeSymbol sourceProxyInterface, string name = null, Accessibility accessibility = Accessibility.Public)
      {
         if (name == null)
         {
            if (sourceProxyInterface.Name.StartsWith("I"))
               name = sourceProxyInterface.Name.Substring(1) + "Proxy";
            else
               name = sourceProxyInterface.Name + "Proxy";
         }

         // Resolve the callback contract if any
         ITypeSymbol serviceContractAttributeType = RequireTypeSymbol("System.ServiceModel.ServiceContractAttribute");
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
            baseType = RequireTypeSymbol("System.ServiceModel.DuplexClientBase`1").Construct(sourceProxyInterface);
         }
         else
         {
            baseType = RequireTypeSymbol("System.ServiceModel.ClientBase`1").Construct(sourceProxyInterface);
         }

         // Create class declaration
         SyntaxNode targetClass = Context.Generator.ClassDeclaration(name, accessibility: accessibility, baseType: Context.Generator.TypeExpression(baseType), interfaceTypes: new[] { Context.Generator.TypeExpression(sourceProxyInterface) });

         // Copy constructors from base class.
         foreach (var baseCtor in baseType.Constructors.Where(ctor => ctor.DeclaredAccessibility != Accessibility.Private))
         {
            var targetCtor = Context.Generator.ConstructorDeclaration(baseCtor, baseCtor.Parameters.Select(p => Context.Generator.Argument(Context.Generator.IdentifierName(p.Name))));
            targetCtor = Context.Generator.WithAccessibility(targetCtor, Accessibility.Public);
            targetClass = Context.Generator.AddMembers(targetClass, targetCtor).AddNewLineTrivia().AddNewLineTrivia();
         }

         foreach (IMethodSymbol sourceMethod in GetOperationContractMethods(sourceProxyInterface))
         {
            SyntaxNode targetMethod = Context.Generator.MethodDeclaration(sourceMethod);
            targetMethod = Context.Generator.WithModifiers(targetMethod, DeclarationModifiers.None);

            bool isVoid = sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(VoidTaskType);
            targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();

            var expression = Context.Generator.InvocationExpression(
               Context.Generator.MemberAccessExpression(
                  Context.Generator.MemberAccessExpression(
                     Context.Generator.BaseExpression(),
                     "Channel"
                  ),
                  sourceMethod.Name
               ),
               sourceMethod.Parameters.Select(p => Context.Generator.IdentifierName(p.Name)).ToArray()
            );

            SyntaxNode statement;
            if (!isVoid)
               statement = Context.Generator.ReturnStatement(expression);
            else
               statement = Context.Generator.ExpressionStatement(expression);

            targetMethod = Context.Generator.WithStatements(targetMethod,
               new[]
               {
                  statement
               }
            );
            targetClass = Context.Generator.AddMembers(targetClass, targetMethod);
         }

         return (ClassDeclarationSyntax)targetClass;
      }
   }   
}
