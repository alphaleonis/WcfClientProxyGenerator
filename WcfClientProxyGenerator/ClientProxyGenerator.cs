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
using Alphaleonis.Vsx.Roslyn.CSharp;
using Alphaleonis.Vsx.Roslyn;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      public static async Task<CompilationUnitSyntax> Generate(Document sourceDocument, CancellationToken cancellationToken)
      {
         Compilation compilation = await sourceDocument.Project.GetCompilationAsync(cancellationToken);
         CompilationUnitSyntax compilationUnit = await sourceDocument.GetSyntaxRootAsync() as CompilationUnitSyntax;
         ClientProxyGenerator generator = new ClientProxyGenerator();

         CompilationUnitSyntax targetCompilationUnit = SyntaxFactory.CompilationUnit(compilationUnit.Externs, compilationUnit.Usings, SyntaxFactory.List<AttributeListSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());
         targetCompilationUnit = targetCompilationUnit.AddMembers((await GenerateProxyInterfaces(sourceDocument, generator, cancellationToken)).ToArray());
         sourceDocument = AddCompilationUnitToProject(sourceDocument, targetCompilationUnit);
         targetCompilationUnit = targetCompilationUnit.AddMembers((await GenerateProxyClasses(sourceDocument, generator, cancellationToken)).ToArray());
         return targetCompilationUnit;
      }

      private static Document AddCompilationUnitToProject(Document document, CompilationUnitSyntax compilationUnit)
      {
         return document.Project.AddDocument(Guid.NewGuid().ToString() + ".g.cs", compilationUnit).Project.GetDocument(document.Id);
      }

      private static async Task<IReadOnlyList<MemberDeclarationSyntax>> GenerateProxyClasses(Document sourceDocument, ClientProxyGenerator generator, CancellationToken cancellationToken)
      {
         CompilationUnitSyntax compilationUnit = await sourceDocument.GetSyntaxRootAsync() as CompilationUnitSyntax;
         SemanticModel semanticModel = await sourceDocument.GetSemanticModelAsync(cancellationToken);
         SyntaxGenerator syntaxGenerator = SyntaxGenerator.GetGenerator(sourceDocument);

         var sourceClasses = compilationUnit.DescendantNodes().OfType<ClassDeclarationSyntax>();

         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;

         foreach (var sourceClass in sourceClasses)
         {
            var sourceClassSymbol = semanticModel.GetDeclaredSymbol(sourceClass);

            ProxyGenerationOptions options = GetGenerationOptionsFromAttribute<ProxyGenerationOptions>(sourceClassSymbol, ProxyGenerationOptions.GenerateProxyAttributeName, ProxyGenerationOptions.GenerateErrorHandlingProxyAttributeName, ProxyGenerationOptions.GenerateErrorHandlingProxyWrapperAttributeName);
            
            if (options != null)
            {
               INamedTypeSymbol serviceInterfaceSymbol;
               serviceInterfaceSymbol = ResolveServiceInterface(semanticModel.Compilation, sourceClassSymbol, options);

               if (!sourceClass.IsPartial())
               {
                  throw new CodeGeneratorException(sourceClass, $"The class {sourceClassSymbol.Name} must be partial to participate in generation.");
               }

               ClassDeclarationSyntax targetClass;
               IEnumerable<IMethodSymbol> dummy;
               if (options.AttributeName == ProxyGenerationOptions.GenerateProxyAttributeName)
                  targetClass = generator.GenerateProxyClass(semanticModel, syntaxGenerator, serviceInterfaceSymbol, sourceClassSymbol.Name, sourceClassSymbol.DeclaredAccessibility, options.SuppressWarningComments, options.ConstructorVisibility, out dummy);
               else
                  targetClass = await generator.GenerateClientClass(semanticModel, syntaxGenerator, serviceInterfaceSymbol, sourceClassSymbol.Name, sourceClassSymbol.DeclaredAccessibility, true, options.SuppressWarningComments, options.ConstructorVisibility, options.AttributeName == ProxyGenerationOptions.GenerateErrorHandlingProxyAttributeName);

               targetClass = syntaxGenerator.AddModifiers(targetClass, DeclarationModifiers.Partial);

               members = members.Add(CreateEnclosingMembers(semanticModel, syntaxGenerator, sourceClass, targetClass));
            }
         }

         return members;
      }

      private static async Task<ImmutableList<MemberDeclarationSyntax>> GenerateProxyInterfaces(Document sourceDocument, ClientProxyGenerator generator, CancellationToken cancellationToken)
      {
         CompilationUnitSyntax compilationUnit = await sourceDocument.GetCompilationUnitRootAsync(cancellationToken);
         IEnumerable<InterfaceDeclarationSyntax> sourceInterfaces = compilationUnit.DescendantNodes().OfType<InterfaceDeclarationSyntax>();

         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;
         SemanticModel semanticModel = await sourceDocument.GetSemanticModelAsync(cancellationToken);
         SyntaxGenerator syntaxGenerator = SyntaxGenerator.GetGenerator(sourceDocument);

         foreach (var sourceInterface in sourceInterfaces)
         {
            ITypeSymbol sourceInterfaceSymbol = semanticModel.GetDeclaredSymbol(sourceInterface);

            var options = GetGenerationOptionsFromAttribute<ProxyGenerationOptions>(sourceInterfaceSymbol, ProxyGenerationOptions.GenerateProxyAttributeName);
            if (options != null)
            {
               INamedTypeSymbol serviceInterfaceSymbol;
               serviceInterfaceSymbol = ResolveServiceInterface(semanticModel.Compilation, sourceInterfaceSymbol, options);

               if (!sourceInterface.IsPartial())
               {
                  throw new CodeGeneratorException(sourceInterface, $"The interface {sourceInterfaceSymbol.Name} must be partial to participate in generation.");
               }

               bool implementsSourceInterface = sourceInterfaceSymbol.AllInterfaces.Any(i => i.Equals(serviceInterfaceSymbol));

               InterfaceDeclarationSyntax targetInterface = generator.GenerateProxyInterface(semanticModel, syntaxGenerator, serviceInterfaceSymbol, sourceInterfaceSymbol.Name, sourceInterfaceSymbol.DeclaredAccessibility, implementsSourceInterface, options.SuppressAsyncMethods, options.SuppressWarningComments);

               targetInterface = syntaxGenerator.AddModifiers(targetInterface, DeclarationModifiers.Partial);

               members = members.Add(CreateEnclosingMembers(semanticModel, syntaxGenerator, sourceInterface, targetInterface));
            }
         }

         return members;
      }

      private static INamedTypeSymbol ResolveServiceInterface(Compilation compilation, ITypeSymbol sourceInterfaceSymbol, ProxyGenerationOptions options)
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
               throw new CodeGeneratorException(sourceInterfaceSymbol, $"Unable to locate the source interface \"{options.SourceInterfaceTypeName}\" specified.");

            if (serviceInterfaceSymbol.TypeKind != TypeKind.Interface)
               throw new CodeGeneratorException(sourceInterfaceSymbol, $"The source interface type specified ({options.SourceInterfaceTypeName}) is not an interface.");
         }
         else
         {
            if (sourceInterfaceSymbol.Interfaces.Length == 1)
            {
               serviceInterfaceSymbol = sourceInterfaceSymbol.Interfaces[0];
            }
            else
            {
               throw new CodeGeneratorException(sourceInterfaceSymbol, $"Unable to determine the source interface for generation. The interface derives from multiple interfaces. Ensure it derives from only one interface, or specify the SourceInterfaceTypeName attribute.");
            }
         }

         return serviceInterfaceSymbol;
      }

      private static T GetGenerationOptionsFromAttribute<T>(ITypeSymbol source, params string[] attributeNames) where T : class
      {
         var attributes = source.GetAttributes().Where(attr => attributeNames.Any(name => attr.AttributeClass.Name.Equals(name))).ToImmutableArray();

         if (attributes.Length > 1)
            throw new CodeGeneratorException(source, $"The {source.TypeKind} '{source.Name}' is decorated with multiple attributes of type {StringUtils.Join(attributeNames, ", ", " or ")}. Only one such attribute is allowed.");

         if (attributes.Length == 0)
            return null;


         return AttributeParser.CreateInstanceFromAttribute<T>(attributes[0]);
      }

      private static MemberDeclarationSyntax CreateEnclosingMembers(SemanticModel semanticModel, SyntaxGenerator generator, MemberDeclarationSyntax sourceMember, MemberDeclarationSyntax targetMember)
      {
         ISymbol sourceMemberSymbol = semanticModel.GetDeclaredSymbol(sourceMember);
         MemberDeclarationSyntax result = targetMember;
         
         while (sourceMember.Parent is ClassDeclarationSyntax)
         {
            result = (MemberDeclarationSyntax)generator.ClassDeclaration(generator.GetName(sourceMember.Parent), 
               accessibility: generator.GetAccessibility(sourceMember.Parent),
               modifiers: generator.GetModifiers(sourceMember.Parent).WithPartial(true), 
               members: new[] { result });

            sourceMember = sourceMember.Parent as MemberDeclarationSyntax;
         }

         if (sourceMemberSymbol.ContainingNamespace != null && !sourceMemberSymbol.ContainingNamespace.IsGlobalNamespace)
         {
            var targetNamespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.IdentifierName(sourceMemberSymbol.ContainingNamespace.GetFullName()), SyntaxFactory.List<ExternAliasDirectiveSyntax>(), SyntaxFactory.List<UsingDirectiveSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());
            result = targetNamespace.AddMembers(result);
         }

         return result;
      }

      #region Properties

      private INamedTypeSymbol GetOperationContractAttributeType(Compilation compilation)
      {
         return compilation.RequireTypeByMetadataName("System.ServiceModel.OperationContractAttribute");
      }

      #endregion

   }
}
