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

      private ImmutableList<MethodDeclarationSyntax> GetWcfClientProxyInterfaceMethods(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol serviceInterface, bool includeAttributes, bool includeSourceInterfaceMethods, bool excludeAsyncMethods)
      {
         ImmutableList<MethodDeclarationSyntax> methods = ImmutableList<MethodDeclarationSyntax>.Empty;
         var genericTaskType = GetGenericTaskType(context);            
         var voidTaskType = GetVoidTaskType(context);

         foreach (IMethodSymbol sourceMethod in serviceInterface.GetAllMembers().OfType<IMethodSymbol>())
         {
            AttributeData operationAttribute = sourceMethod.GetAttributes().Where(attr => attr.AttributeClass.Equals(GetOperationContractAttributeType(context))).FirstOrDefault();
            var nonOperationAttributes = sourceMethod.GetAttributes().Where(attr => !attr.AttributeClass.Equals(GetOperationContractAttributeType(context)));
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
                     returnType = context.Compilation.GetSpecialType(SpecialType.System_Void);
                  }
                  else
                  {
                     INamedTypeSymbol namedReturnType = returnType as INamedTypeSymbol;
                     if (namedReturnType == null)
                        throw new TextFileGeneratorException(sourceMethod, $"Unexpected return type (not a named type) in async method. Expected System.Threading.Tasks.Task.");

                     if (IsGenericTaskType(context, namedReturnType))
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
                  SyntaxNode targetMethod = context.Generator.MethodDeclaration(sourceMethod);
                  targetMethod = context.Generator.WithType(targetMethod, context.Generator.TypeExpression(returnType));
                  targetMethod = context.Generator.WithName(targetMethod, methodName);
                  if (includeAttributes)
                  {
                     targetMethod = context.Generator.AddAttributes(targetMethod, nonOperationAttributes.Select(a => context.Generator.Attribute(a)));
                     if (sourceIsAsync)
                     {
                        targetMethod = context.Generator.AddAttributes(targetMethod,
                           context.Generator.Attribute(
                              context.Generator.TypeExpression(GetOperationContractAttributeType(context)),
                           operationAttribute.NamedArguments.Where(arg => arg.Key != "AsyncPattern").Select(arg => context.Generator.AttributeArgument(arg.Key, context.Generator.TypedConstantExpression(arg.Value)))));
                     }
                     else
                     {
                        targetMethod = context.Generator.AddAttributes(targetMethod, context.Generator.Attribute(operationAttribute));
                     }
                  }
                  targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
                  methods = methods.Add((MethodDeclarationSyntax)targetMethod);
               }

               if (!excludeAsyncMethods && (includeSourceInterfaceMethods || !sourceIsAsync))
               {
                  SyntaxNode targetMethod = context.Generator.MethodDeclaration(sourceMethod);
                  targetMethod = context.Generator.WithType(targetMethod,
                     context.Generator.TypeExpression(returnType.SpecialType == SpecialType.System_Void ? voidTaskType : genericTaskType.Construct(returnType)));
                  targetMethod = context.Generator.WithName(targetMethod, methodName + "Async");
                  if (includeAttributes)
                  {
                     targetMethod = context.Generator.AddAttributes(targetMethod, nonOperationAttributes.Select(a => context.Generator.Attribute(a)));
                     if (sourceIsAsync)
                     {
                        targetMethod = context.Generator.AddAttributes(targetMethod, context.Generator.Attribute(operationAttribute));
                     }
                     else
                     {
                        targetMethod = context.Generator.AddAttributes(targetMethod,
                           context.Generator.Attribute(context.Generator.TypeExpression(GetOperationContractAttributeType(context)),
                           operationAttribute.NamedArguments
                              .Where(arg => arg.Key != "AsyncPattern")
                              .Select(arg => context.Generator.AttributeArgument(arg.Key, context.Generator.TypedConstantExpression(arg.Value)))
                              .Concat(new[] { context.Generator.AttributeArgument("AsyncPattern", context.Generator.TrueLiteralExpression()) })));
                     }
                  }
                  targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
                  methods = methods.Add((MethodDeclarationSyntax)targetMethod);
               }
            }
         }
         return methods;
      }

      private INamedTypeSymbol GetVoidTaskType(CSharpRoslynCodeGenerationContext context)
      {
         return RequireTypeSymbol<Task>(context);
      }

      private INamedTypeSymbol GetGenericTaskType(CSharpRoslynCodeGenerationContext context)
      {
         return RequireTypeSymbol(context, typeof(Task<>));
      }      

      private bool IsGenericTaskType(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol namedReturnType)
      {
         return namedReturnType.IsGenericType && namedReturnType.ConstructUnboundGenericType().Equals(RequireTypeSymbol(context, typeof(Task<>)).ConstructUnboundGenericType());
      }

      private INamedTypeSymbol RequireTypeSymbol<T>(CSharpRoslynCodeGenerationContext context)
      {
         return RequireTypeSymbol(context, typeof(T));
      }

      private INamedTypeSymbol RequireTypeSymbol(CSharpRoslynCodeGenerationContext context, Type type)
      {
         INamedTypeSymbol symbol = context.Compilation.GetTypeByMetadataName(type.FullName);
         if (symbol == null)
            throw new TextFileGeneratorException($"Unable to find the required type {type.AssemblyQualifiedName} in this context. Are you missing an assembly reference?");

         return symbol;
      }

      private INamedTypeSymbol RequireTypeSymbol(CSharpRoslynCodeGenerationContext context, string fullyQualifiedTypeName)
      {
         INamedTypeSymbol symbol = context.Compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
         if (symbol == null)
            throw new TextFileGeneratorException($"Unable to find the required type {fullyQualifiedTypeName} in this context. Are you missing an assembly reference?");

         return symbol;
      }

      private T AddGeneratedCodeAttribute<T>(SyntaxGenerator g, T node) where T : SyntaxNode
      {
         return (T)g.AddAttributes(node,
            g.Attribute("System.CodeDom.Compiler.GeneratedCodeAttribute",
               g.LiteralExpression(GetCodeGeneratorName()),
               g.LiteralExpression(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>().Version)
            )
         );
      }

      private static string GetCodeGeneratorName()
      {
         return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>().Title;
      }

      private IEnumerable<IMethodSymbol> GetOperationContractMethods(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol proxyInterface)
      {
         return proxyInterface.GetAllMembers().OfType<IMethodSymbol>().Where(member => member.GetAttributes().Any(attr => attr.AttributeClass.Equals(GetOperationContractAttributeType(context))));
      }

      private bool ReturnsTask(CSharpRoslynCodeGenerationContext context, IMethodSymbol sourceMethod)
      {
         return sourceMethod.ReturnType.Equals(GetVoidTaskType(context)) || IsGenericTaskType(context, ((INamedTypeSymbol)sourceMethod.ReturnType));
      }

      private bool IsVoid(CSharpRoslynCodeGenerationContext context, IMethodSymbol sourceMethod)
      {
         return sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(GetVoidTaskType(context));
      }

      private SyntaxNode AwaitExpressionIfAsync(SyntaxGenerator g, bool isAsync, SyntaxNode expression, bool configureAwait = false)
      {
         if (isAsync)
            return AwaitExpression(g, expression, configureAwait);
         else
            return expression;
      }

      private SyntaxNode AwaitExpression(SyntaxGenerator g, SyntaxNode expression, bool configureAwait = false)
      {
         return g.AwaitExpression(
            g.InvocationExpression(
               g.MemberAccessExpression(
                  expression,
                  "ConfigureAwait"
               ),
               g.LiteralExpression(configureAwait)
            )
         );
      }


      #endregion
   }
}
