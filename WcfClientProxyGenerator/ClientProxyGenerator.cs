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

// TODO: Add Warning Comments about code being generated.
// TODO: Attribute should reuqire the type or type-name as the first argument! Can we find a type that is not referenced? If not, add a method to search for it!
namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      private const string GenerateWcfClientAttributeName = "GenerateWcfClientAttribute";

      private static CSharpRoslynCodeGenerationContext AddType(CSharpRoslynCodeGenerationContext context, CompilationUnitSyntax newCu)
      {
         return context.WithDocument(context.Document.Project.AddDocument(Guid.NewGuid().ToString() + ".g.cs", newCu).Project.GetDocument(context.Document.Id));
      }

      private static IReadOnlyList<MemberDeclarationSyntax> GenerateProxyClasses(CSharpRoslynCodeGenerationContext context, ClientProxyGenerator generator)
      {
         var sourceClasses = context.CompilationUnit.TopLevelClasses();

         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;

         foreach (var sourceClass in sourceClasses)
         {
            var sourceClassSymbol = context.SemanticModel.GetDeclaredSymbol(sourceClass);

            var generationAttribute = GetGenerationAttribute(sourceClassSymbol);
            if (generationAttribute != null)
            {
               GenerationOptions options = new GenerationOptions(generationAttribute);
               INamedTypeSymbol serviceInterfaceSymbol;
               serviceInterfaceSymbol = ResolveServiceInterface(context.Compilation, sourceClassSymbol, options);

               if (!sourceClass.IsPartial())
               {
                  throw new TextFileGeneratorException(sourceClass, $"The class {sourceClassSymbol.Name} must be partial to participate in generation.");
               }

               ClassDeclarationSyntax targetClass;
               if (options.Wrapper)
                  targetClass = generator.GenerateClientClass(context, serviceInterfaceSymbol, sourceClassSymbol.Name, sourceClassSymbol.DeclaredAccessibility, true);
               else
                  targetClass = generator.GenerateProxyClass(context, serviceInterfaceSymbol, sourceClassSymbol.Name, sourceClassSymbol.DeclaredAccessibility);

               targetClass = context.Generator.AddModifiers(targetClass, DeclarationModifiers.Partial);

               MemberDeclarationSyntax result = targetClass;
               if (sourceClassSymbol.ContainingNamespace != null && !sourceClassSymbol.ContainingNamespace.IsGlobalNamespace)
               {
                  var targetNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.IdentifierName(sourceClassSymbol.ContainingNamespace.GetFullName()), SyntaxFactory.List<ExternAliasDirectiveSyntax>(), SyntaxFactory.List<UsingDirectiveSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());
                  result = targetNamespace.AddMembers(result);
               }

               members = members.Add(result);
            }
         }

         return members;
      }

      private static IReadOnlyList<MemberDeclarationSyntax> GenerateProxyInterfaces(CSharpRoslynCodeGenerationContext context, ClientProxyGenerator generator)
      {
         var sourceInterfaces = context.CompilationUnit.TopLevelInterfaces();
         
         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;

         foreach (var sourceInterface in sourceInterfaces)
         {
            var sourceInterfaceSymbol = context.SemanticModel.GetDeclaredSymbol(sourceInterface);

            var generationAttribute = GetGenerationAttribute(sourceInterfaceSymbol);
            if (generationAttribute != null)
            {
               GenerationOptions options = new GenerationOptions(generationAttribute);

               INamedTypeSymbol serviceInterfaceSymbol;
               serviceInterfaceSymbol = ResolveServiceInterface(context.Compilation, sourceInterfaceSymbol, options);

               if (!sourceInterface.IsPartial())
               {
                  throw new TextFileGeneratorException(sourceInterface, $"The interface {sourceInterfaceSymbol.Name} must be partial to participate in generation.");
               }

               bool implementsSourceInterface = sourceInterfaceSymbol.AllInterfaces.Any(i => i.Equals(serviceInterfaceSymbol));

               InterfaceDeclarationSyntax targetInterface = generator.GenerateProxyInterface(context.SemanticModel, context.Generator, serviceInterfaceSymbol, sourceInterfaceSymbol.Name, sourceInterfaceSymbol.DeclaredAccessibility, implementsSourceInterface, options.SuppressAsyncMethods);

               targetInterface = context.Generator.AddModifiers(targetInterface, DeclarationModifiers.Partial);

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

      private static INamedTypeSymbol ResolveServiceInterface(Compilation compilation, INamedTypeSymbol sourceInterfaceSymbol, GenerationOptions options)
      {
         INamedTypeSymbol serviceInterfaceSymbol;
         if (options.SourceInterfaceType != null)
         {
            serviceInterfaceSymbol = options.SourceInterfaceType;
         }
         else if (!String.IsNullOrEmpty(options.SourceInterfaceTypeName))
         {
            serviceInterfaceSymbol = compilation.GetTypeByMetadataName(options.SourceInterfaceTypeName);

            if (serviceInterfaceSymbol == null)
               throw new TextFileGeneratorException(sourceInterfaceSymbol, $"Unable to locate the source interface \"{options.SourceInterfaceTypeName}\" specified.");

            if (serviceInterfaceSymbol.TypeKind != TypeKind.Interface)
               throw new TextFileGeneratorException(sourceInterfaceSymbol, $"The source interface type specified ({options.SourceInterfaceTypeName}) is not an interface.");
         }
         else
         {
            if (sourceInterfaceSymbol.Interfaces.Length == 1)
            {
               serviceInterfaceSymbol = sourceInterfaceSymbol.Interfaces[0];
            }
            else
            {
               throw new TextFileGeneratorException(sourceInterfaceSymbol, $"Unable to determine the source interface for generation. The interface derives from multiple interfaces. Ensure it derives from only one interface, or specify the SourceInterfaceTypeName attribute.");
            }
         }

         return serviceInterfaceSymbol;
      }

      private static AttributeData GetGenerationAttribute(INamedTypeSymbol source)
      {
         var result = source.GetAttributes().Where(attr => attr.AttributeClass.Name.Equals(GenerateWcfClientAttributeName)).ToImmutableArray();

         if (result.Length > 1)
            throw new TextFileGeneratorException(source, $"The {source.TypeKind} '{source.Name}' is decorated with multiple attributes of type '{GenerateWcfClientAttributeName}'. Only one such attribute is allowed.");

         if (result.Length == 0)
            return null;
         
         return result[0];
      }

      public static async Task<CompilationUnitSyntax> Generate(CSharpRoslynCodeGenerationContext context)
      {
         ClientProxyGenerator generator = new ClientProxyGenerator();

         //context = RemoveGeneratedPartsOfMemberDeclaration(context, cu => cu.TopLevelInterfaces());
         //context = RemoveGeneratedPartsOfMemberDeclaration(context, cu => cu.TopLevelClasses());

         CompilationUnitSyntax targetCompilationUnit = SyntaxFactory.CompilationUnit(context.CompilationUnit.Externs, context.CompilationUnit.Usings, SyntaxFactory.List<AttributeListSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());
         targetCompilationUnit = targetCompilationUnit.AddMembers(GenerateProxyInterfaces(context, generator).ToArray());
         context = AddType(context, targetCompilationUnit);
         targetCompilationUnit = targetCompilationUnit.AddMembers(GenerateProxyClasses(context, generator).ToArray());         
         return targetCompilationUnit;
      }

      #region Properties

      //public CSharpRoslynCodeGenerationContext Context { get; }

      private INamedTypeSymbol GetOperationContractAttributeType(CSharpRoslynCodeGenerationContext context)
      {
         return RequireTypeSymbol(context, "System.ServiceModel.OperationContractAttribute");
      }

      #endregion

   }
}
