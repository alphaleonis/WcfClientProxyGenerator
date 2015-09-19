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

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      #region Utility Methods

      private ImmutableList<MethodDeclarationSyntax> GetWcfClientProxyInterfaceMethods(INamedTypeSymbol serviceInterface, bool includeAttributes, bool includeSourceInterfaceMethods, bool excludeAsyncMethods)
      {
         ImmutableList<MethodDeclarationSyntax> methods = ImmutableList<MethodDeclarationSyntax>.Empty;
         var genericTaskType = RequireTypeSymbol(typeof(Task<>));
         var voidTaskType = VoidTaskType;

         foreach (IMethodSymbol sourceMethod in serviceInterface.GetAllMembers().OfType<IMethodSymbol>())
         {
            AttributeData operationAttribute = sourceMethod.GetAttributes().Where(attr => attr.AttributeClass.Equals(OperationContractAttributeType)).FirstOrDefault();
            var nonOperationAttributes = sourceMethod.GetAttributes().Where(attr => !attr.AttributeClass.Equals(OperationContractAttributeType));
            if (operationAttribute != null)
            {
               bool sourceIsAsync = false;

               var asyncPatternArgument = operationAttribute.NamedArguments.FirstOrDefault(a => a.Key.Equals("AsyncPattern"));
               if (asyncPatternArgument.Key != null)
               {
                  if (asyncPatternArgument.Value.Value is Boolean)
                     sourceIsAsync = (bool)asyncPatternArgument.Value.Value;
               }

               string methodName = sourceMethod.Name;
               ITypeSymbol returnType = sourceMethod.ReturnType;

               // Resolve types if method is async.
               if (sourceIsAsync)
               {
                  if (methodName.EndsWith("Async"))
                     methodName = methodName.Substring(0, methodName.Length - "Async".Length);

                  if (returnType.Equals(voidTaskType))
                  {
                     returnType = Context.Compilation.GetSpecialType(SpecialType.System_Void);
                  }
                  else
                  {
                     INamedTypeSymbol namedReturnType = returnType as INamedTypeSymbol;
                     if (namedReturnType == null)
                        throw new TextFileGeneratorException(sourceMethod, $"Unexpected return type (not a named type) in async method. Expected System.Threading.Tasks.Task.");

                     if (IsGenericTaskType(namedReturnType))
                     {
                        returnType = namedReturnType.TypeArguments[0];
                     }
                     else
                     {
                        throw new TextFileGeneratorException(sourceMethod, $"Unexpected return type from AsyncPattern OperationContract {sourceMethod.Name}; Expected System.Threading.Tasks.Task.");
                     }
                  }
               }

               // Create Non-async version of method.
               if (includeSourceInterfaceMethods || sourceIsAsync)
               {
                  SyntaxNode targetMethod = Context.Generator.MethodDeclaration(sourceMethod);
                  targetMethod = Context.Generator.WithType(targetMethod, Context.Generator.TypeExpression(returnType));
                  targetMethod = Context.Generator.WithName(targetMethod, methodName);
                  if (includeAttributes)
                  {
                     targetMethod = Context.Generator.AddAttributes(targetMethod, nonOperationAttributes.Select(a => Context.Generator.Attribute(a)));
                     if (sourceIsAsync)
                     {
                        targetMethod = Context.Generator.AddAttributes(targetMethod,
                           Context.Generator.Attribute(
                              Context.Generator.TypeExpression(OperationContractAttributeType),
                           operationAttribute.NamedArguments.Where(arg => arg.Key != "AsyncPattern").Select(arg => Context.Generator.AttributeArgument(arg.Key, Context.Generator.TypedConstantExpression(arg.Value)))));
                     }
                     else
                     {
                        targetMethod = Context.Generator.AddAttributes(targetMethod, Context.Generator.Attribute(operationAttribute));
                     }
                  }
                  targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
                  methods = methods.Add((MethodDeclarationSyntax)targetMethod);
               }

               if (!excludeAsyncMethods && (includeSourceInterfaceMethods || !sourceIsAsync))
               {
                  SyntaxNode targetMethod = Context.Generator.MethodDeclaration(sourceMethod);
                  targetMethod = Context.Generator.WithType(targetMethod,
                     Context.Generator.TypeExpression(returnType.SpecialType == SpecialType.System_Void ? voidTaskType : genericTaskType.Construct(returnType)));
                  targetMethod = Context.Generator.WithName(targetMethod, methodName + "Async");
                  if (includeAttributes)
                  {
                     targetMethod = Context.Generator.AddAttributes(targetMethod, nonOperationAttributes.Select(a => Context.Generator.Attribute(a)));
                     if (sourceIsAsync)
                     {
                        targetMethod = Context.Generator.AddAttributes(targetMethod, Context.Generator.Attribute(operationAttribute));
                     }
                     else
                     {
                        targetMethod = Context.Generator.AddAttributes(targetMethod,
                           Context.Generator.Attribute(Context.Generator.TypeExpression(OperationContractAttributeType),
                           operationAttribute.NamedArguments
                              .Where(arg => arg.Key != "AsyncPattern")
                              .Select(arg => Context.Generator.AttributeArgument(arg.Key, Context.Generator.TypedConstantExpression(arg.Value)))
                              .Concat(new[] { Context.Generator.AttributeArgument("AsyncPattern", Context.Generator.TrueLiteralExpression()) })));
                     }
                  }
                  targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
                  methods = methods.Add((MethodDeclarationSyntax)targetMethod);
               }
            }
         }
         return methods;
      }

      private INamedTypeSymbol VoidTaskType
      {
         get
         {
            return RequireTypeSymbol<Task>();
         }
      }

      private INamedTypeSymbol GenericTaskType
      {
         get

         {
            return RequireTypeSymbol(typeof(Task<>));
         }
      }      

      private bool IsGenericTaskType(INamedTypeSymbol namedReturnType)
      {
         return namedReturnType.IsGenericType && namedReturnType.ConstructUnboundGenericType().Equals(RequireTypeSymbol(typeof(Task<>)).ConstructUnboundGenericType());
      }

      private INamedTypeSymbol RequireTypeSymbol<T>()
      {
         return RequireTypeSymbol(typeof(T));
      }

      private INamedTypeSymbol RequireTypeSymbol(Type type)
      {
         INamedTypeSymbol symbol = Context.Compilation.GetTypeByMetadataName(type.FullName);
         if (symbol == null)
            throw new TextFileGeneratorException($"Unable to find the required type {type.AssemblyQualifiedName} in this context. Are you missing an assembly reference?");

         return symbol;
      }

      private INamedTypeSymbol RequireTypeSymbol(string fullyQualifiedTypeName)
      {
         INamedTypeSymbol symbol = Context.Compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
         if (symbol == null)
            throw new TextFileGeneratorException($"Unable to find the required type {fullyQualifiedTypeName} in this context. Are you missing an assembly reference?");

         return symbol;
      }

      private T AddGeneratedCodeAttribute<T>(T node) where T : SyntaxNode
      {
         return (T)Context.Generator.AddAttributes(node,
            Context.Generator.Attribute("System.CodeDom.Compiler.GeneratedCodeAttribute",
               Context.Generator.LiteralExpression(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>().Title),
               Context.Generator.LiteralExpression(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>().Version)
            )
         );
      }

      private IEnumerable<IMethodSymbol> GetOperationContractMethods(INamedTypeSymbol proxyInterface)
      {
         return proxyInterface.GetAllMembers().OfType<IMethodSymbol>().Where(member => member.GetAttributes().Any(attr => attr.AttributeClass.Equals(OperationContractAttributeType)));
      }

      private bool ReturnsTask(IMethodSymbol sourceMethod)
      {
         return sourceMethod.ReturnType.Equals(VoidTaskType) || IsGenericTaskType(((INamedTypeSymbol)sourceMethod.ReturnType));
      }

      private bool IsVoid(IMethodSymbol sourceMethod)
      {
         return sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(VoidTaskType);
      }
      private SyntaxNode AwaitExpressionIfAsync(bool isAsync, SyntaxNode expression, bool configureAwait = false)
      {
         if (isAsync)
            return AwaitExpression(expression, configureAwait);
         else
            return expression;
      }

      private SyntaxNode AwaitExpression(SyntaxNode expression, bool configureAwait = false)
      {
         return Context.Generator.AwaitExpression(
            Context.Generator.InvocationExpression(
               Context.Generator.MemberAccessExpression(
                  expression,
                  "ConfigureAwait"
               ),
               Context.Generator.LiteralExpression(configureAwait)
            )
         );
      }


      #endregion
   }
}
