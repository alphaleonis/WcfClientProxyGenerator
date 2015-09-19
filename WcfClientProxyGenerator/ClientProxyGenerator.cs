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
      private const string WcfClientProxyCodeGenerationAttributeName = "WcfProxyInterfaceGenerationOptions";

      #region Constructor

      public ClientProxyGenerator(CSharpRoslynCodeGenerationContext context)
      {
         if (context == null)
            throw new ArgumentNullException(nameof(context), $"{nameof(context)} is null.");

         Context = context;
      }

      #endregion

      #region Properties

      public CSharpRoslynCodeGenerationContext Context { get; }

      private INamedTypeSymbol OperationContractAttributeType
      {
         get
         {
            return RequireTypeSymbol("System.ServiceModel.OperationContractAttribute");
         }
      }

      #endregion
      

      public static async Task<CompilationUnitSyntax> Generate(CSharpRoslynCodeGenerationContext context)
      {
         ClientProxyGenerator generator = new ClientProxyGenerator(context);

         CompilationUnitSyntax targetCompilationUnit = SyntaxFactory.CompilationUnit(context.CompilationUnit.Externs, context.CompilationUnit.Usings, SyntaxFactory.List<AttributeListSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());

         targetCompilationUnit = targetCompilationUnit.AddMembers(GenerateProxyInterfaces(context, generator).ToArray());

         return targetCompilationUnit;
      }

      private static IReadOnlyList<MemberDeclarationSyntax> GenerateProxyInterfaces(CSharpRoslynCodeGenerationContext context, ClientProxyGenerator generator)
      {
         var sourceInterfaces = context.CompilationUnit.TopLevelInterfaces();

         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;

         foreach (var sourceInterface in sourceInterfaces)
         {
            var sourceInterfaceSymbol = context.SemanticModel.GetDeclaredSymbol(sourceInterface);

            var generationAttributes = sourceInterfaceSymbol.GetAttributes().Where(attr => attr.AttributeClass.Name.Equals(WcfClientProxyCodeGenerationAttributeName)).ToArray();
            if (generationAttributes.Length > 1)
               throw new TextFileGeneratorException(sourceInterface, $"The interface {sourceInterface.Identifier.Text} is decorated with more than one {WcfClientProxyCodeGenerationAttributeName}. Only one such attribute per interface is allowed.");

            if (generationAttributes.Length == 1)
            {
               AttributeData generationAttribute = generationAttributes[0];

               GenerationOptions options = new GenerationOptions(generationAttribute);


               INamedTypeSymbol serviceInterfaceSymbol;

               if (options.SourceInterfaceType != null)
               {
                  serviceInterfaceSymbol = options.SourceInterfaceType;
               }
               else if (!String.IsNullOrEmpty(options.SourceInterfaceTypeName))
               {
                  serviceInterfaceSymbol = context.GetTypeByMetadataName(options.SourceInterfaceTypeName);

                  if (serviceInterfaceSymbol == null)
                     throw new TextFileGeneratorException(sourceInterface, $"Unable to locate the source interface \"{options.SourceInterfaceTypeName}\" specified.");

                  if (serviceInterfaceSymbol.TypeKind != TypeKind.Interface)
                     throw new TextFileGeneratorException(sourceInterface, $"The source interface type specified ({options.SourceInterfaceTypeName}) is not an interface.");
               }
               else
               {
                  if (sourceInterfaceSymbol.Interfaces.Length == 1)
                  {
                     serviceInterfaceSymbol = sourceInterfaceSymbol.Interfaces[0];
                  }
                  else
                  {
                     throw new TextFileGeneratorException(sourceInterface, $"Unable to determine the source interface for generation. The interface derives from multiple interfaces. Ensure it derives from only one interface, or specify the SourceInterfaceTypeName attribute.");
                  }
               }

               if (!sourceInterface.IsPartial())
               {
                  throw new TextFileGeneratorException(sourceInterface, $"The interface {sourceInterfaceSymbol.Name} must be partial to participate in generation.");
               }

               bool implementsSourceInterface = sourceInterfaceSymbol.AllInterfaces.Any(i => i.Equals(serviceInterfaceSymbol));

               InterfaceDeclarationSyntax targetInterface = generator.GenerateProxyInterface(serviceInterfaceSymbol, sourceInterfaceSymbol.Name, sourceInterfaceSymbol.DeclaredAccessibility, implementsSourceInterface, options.SuppressAsyncMethods);

               MemberDeclarationSyntax result = targetInterface;
               if (sourceInterfaceSymbol.ContainingNamespace != null && !sourceInterfaceSymbol.ContainingNamespace.IsGlobalNamespace)
               {
                  var targetNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.IdentifierName(sourceInterfaceSymbol.ContainingNamespace.GetFullName()), SyntaxFactory.List<ExternAliasDirectiveSyntax>(), SyntaxFactory.List<UsingDirectiveSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());
                  result = targetNamespace.AddMembers(result);
               }

               members = members.Add(result);
            }
         }

         return members;
      }
   }
}
