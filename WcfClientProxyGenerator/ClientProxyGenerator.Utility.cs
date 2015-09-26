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
using Microsoft.CodeAnalysis.Diagnostics;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      #region Utility Methods

      private class OperationContractMethodInfo
      {
         public OperationContractMethodInfo(SemanticModel semanticModel, IMethodSymbol method)
         {
            var taskType = semanticModel.Compilation.RequireTypeByMetadataName(typeof(Task).FullName);
            var genericTaskType = semanticModel.Compilation.RequireTypeByMetadataName(typeof(Task<>).FullName);
            var operationContractAttributeType = semanticModel.Compilation.RequireTypeByMetadataName("System.ServiceModel.OperationContractAttribute");

            var operationContractAttribute = method.GetAttribute(operationContractAttributeType);
            if (operationContractAttribute == null)
               throw new InvalidOperationException($"The method {method.Name} is not decorated with {operationContractAttributeType.GetFullName()}.");

            OperationContractAttribute = new ModifiedOperationContractAttributeData(operationContractAttribute);

            AdditionalAttributes = method.GetAttributes().Where(attr => attr.AttributeClass.Equals(operationContractAttributeType) == false).ToImmutableArray();

            IsAsync = method.ReturnType.Equals(taskType) || method.ReturnType.OriginalDefinition.Equals(genericTaskType.OriginalDefinition);

            ContractReturnsVoid = method.ReturnsVoid || method.ReturnType.Equals(taskType);

            Method = method;

            if (IsAsync && method.Name.EndsWith("Async"))
               ContractMethodName = method.Name.Substring(0, method.Name.Length - "Async".Length);
            else
               ContractMethodName = method.Name;

            if (IsAsync)
            {
               if (ContractReturnsVoid)
                  ContractReturnType = semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);
               else
                  ContractReturnType = ((INamedTypeSymbol)method.ReturnType).TypeArguments.Single();               
            }
            else
            {
               ContractReturnType = ReturnType;
            }

            AsyncReturnType = ContractReturnsVoid ? taskType : genericTaskType.Construct(ContractReturnType);
         }

         #region Properties

         public string MethodName { get { return Method.Name; } }

         public string ContractMethodName { get; }

         public bool ContractReturnsVoid { get; }

         public ITypeSymbol AsyncReturnType { get; }

         public ITypeSymbol ReturnType { get { return Method.ReturnType; } }

         public ITypeSymbol ContractReturnType { get; }

         public bool IsAsync { get; }
         
         public AttributeData OperationContractAttribute { get; }

         public ImmutableArray<AttributeData> AdditionalAttributes { get; }

         public ImmutableArray<IParameterSymbol> Parameters { get { return Method.Parameters; } }

         public IEnumerable<AttributeData> AllAttributes
         {
            get
            {
               return AdditionalAttributes.Concat(new[] { OperationContractAttribute });                              
            }
         }

         public IMethodSymbol Method { get; }

         #endregion

         #region Methods

         public bool ContractMacthes(OperationContractMethodInfo other)
         {
            return ContractMethodName.Equals(other.ContractMethodName) &&
               ContractReturnType.Equals(other.ContractReturnType) &&
               Parameters.Select(p => p.Type).SequenceEqual(other.Parameters.Select(p => p.Type));
         }

         #endregion

         private class ModifiedOperationContractAttributeData : AttributeData
         {
            private readonly AttributeData m_source;

            public ModifiedOperationContractAttributeData(AttributeData source)
            {
               m_source = source;
            }

            protected override SyntaxReference CommonApplicationSyntaxReference
            {
               get
               {
                  return m_source.ApplicationSyntaxReference;
               }
            }

            protected override INamedTypeSymbol CommonAttributeClass
            {
               get
               {
                  return m_source.AttributeClass;
               }
            }

            protected override IMethodSymbol CommonAttributeConstructor
            {
               get
               {
                  return m_source.AttributeConstructor;
               }
            }

            protected override ImmutableArray<TypedConstant> CommonConstructorArguments
            {
               get
               {
                  return m_source.ConstructorArguments;
               }
            }


            protected override ImmutableArray<KeyValuePair<string, TypedConstant>> CommonNamedArguments
            {
               get
               {
                  return m_source.NamedArguments.Where(arg => !arg.Key.Equals("AsyncPattern")).ToImmutableArray();
               }
            }
         }
      }

      private ImmutableArray<OperationContractMethodInfo> GetOperationContractMethods(SemanticModel semanticModel, INamedTypeSymbol serviceInterface)
      {
         ITypeSymbol operationContractAttributeType = semanticModel.Compilation.RequireTypeByMetadataName("System.ServiceModel.OperationContractAttribute");

         var arrayBuilder = ImmutableArray.CreateBuilder<OperationContractMethodInfo>();
         foreach (IMethodSymbol sourceMethod in serviceInterface.GetAllMembers().OfType<IMethodSymbol>().Where(m => m.GetAttribute(operationContractAttributeType) != null))
         {
            arrayBuilder.Add(new OperationContractMethodInfo(semanticModel, sourceMethod));

         }

         return arrayBuilder.ToImmutable();         
      }

      private ImmutableList<MethodDeclarationSyntax> GetOperationContractMethodDeclarations(SemanticModel semanticModel, SyntaxGenerator gen, INamedTypeSymbol serviceInterface, bool includeAttributes, bool includeSourceInterfaceMethods, bool excludeAsyncMethods)
      {
         ImmutableList<MethodDeclarationSyntax> methods = ImmutableList<MethodDeclarationSyntax>.Empty;

         var sourceMethods = GetOperationContractMethods(semanticModel, serviceInterface);

         foreach (var methodInfo in sourceMethods)
         {
            // Emit non-async version of method
            if (!(methodInfo.IsAsync && sourceMethods.Any(m => m.ContractMacthes(methodInfo) && m.IsAsync == false)))
            {
               SyntaxNode targetMethod = gen.MethodDeclaration(methodInfo.Method);
               targetMethod = gen.WithType(targetMethod, gen.TypeExpression(methodInfo.ContractReturnType));
               targetMethod = gen.WithName(targetMethod, methodInfo.ContractMethodName);

               if (includeAttributes)
               {
                  targetMethod = gen.AddAttributes(targetMethod, methodInfo.AllAttributes.Select(a => gen.Attribute(a)));                  
               }
               targetMethod = targetMethod.AddNewLineTrivia().AddNewLineTrivia();
               methods = methods.Add((MethodDeclarationSyntax)targetMethod);
            }

            // Emit async-version of method
            if (!excludeAsyncMethods)
            {
               if (methodInfo.IsAsync || !sourceMethods.Any(m => m.ContractMacthes(methodInfo) && m.IsAsync))
               {
                  SyntaxNode targetMethod = gen.MethodDeclaration(methodInfo.Method);
                  targetMethod = gen.WithType(targetMethod, gen.TypeExpression(methodInfo.AsyncReturnType));
                  targetMethod = gen.WithName(targetMethod, methodInfo.ContractMethodName + "Async");

                  if (includeAttributes)
                  {
                     targetMethod = gen.AddAttributes(targetMethod, methodInfo.AdditionalAttributes.Select(a => gen.Attribute(a)));
                     targetMethod = gen.AddAttributes(targetMethod,
                        gen.AddAttributeArguments(gen.Attribute(methodInfo.OperationContractAttribute), new[] { gen.AttributeArgument("AsyncPattern", gen.TrueLiteralExpression()) })
                     );
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

   internal static class LocalSyntaxGeneratorExtensions
   {
      public static SyntaxNode AddWarningComment(this SyntaxGenerator g, SyntaxNode node)
      {
         return node.AddLeadingTrivia(
            g.Comment("/*****************************************************************/"),
            g.NewLine(),
            g.Comment("/* WARNING! THIS CODE IS AUTOMATICALLY GENERATED. DO NOT MODIFY! */"),
            g.NewLine(),
            g.Comment("/*****************************************************************/")
         );
      }

      public static SyntaxNode AddWarningCommentIf(this SyntaxGenerator g, bool condition, SyntaxNode node)
      {
         if (condition)
            return g.AddWarningComment(node);
         else
            return node;
      }

   }
}
