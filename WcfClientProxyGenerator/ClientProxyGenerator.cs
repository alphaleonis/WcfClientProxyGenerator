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
      public static async Task<CompilationUnitSyntax> Generate(CSharpRoslynCodeGenerationContext context)
      {
         ClientProxyGenerator generator = new ClientProxyGenerator();

         CompilationUnitSyntax targetCompilationUnit = SyntaxFactory.CompilationUnit(context.CompilationUnit.Externs, context.CompilationUnit.Usings, SyntaxFactory.List<AttributeListSyntax>(), SyntaxFactory.List<MemberDeclarationSyntax>());
         targetCompilationUnit = targetCompilationUnit.AddMembers(GenerateProxyInterfaces(context, generator).ToArray());
         context = await AddCompilationUnit(context, targetCompilationUnit);
         targetCompilationUnit = targetCompilationUnit.AddMembers((await GenerateProxyClasses(context, generator)).ToArray());
         return targetCompilationUnit;
      }

      private static Task<CSharpRoslynCodeGenerationContext> AddCompilationUnit(CSharpRoslynCodeGenerationContext context, CompilationUnitSyntax compilationUnit)
      {
         return context.WithDocumentAsync(context.Document.Project.AddDocument(Guid.NewGuid().ToString() + ".g.cs", compilationUnit).Project.GetDocument(context.Document.Id));
      }

      private static async Task<IReadOnlyList<MemberDeclarationSyntax>> GenerateProxyClasses(CSharpRoslynCodeGenerationContext context, ClientProxyGenerator generator)
      {
         var sourceClasses = context.CompilationUnit.DescendantNodes().OfType<ClassDeclarationSyntax>();

         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;

         foreach (var sourceClass in sourceClasses)
         {
            var sourceClassSymbol = context.SemanticModel.GetDeclaredSymbol(sourceClass);

            var generationAttribute = GetGenerationAttribute(sourceClassSymbol);
            if (generationAttribute != null)
            {
               GenerationOptions options = AttributeParser.CreateInstanceFromAttribute<GenerationOptions>(generationAttribute);

               INamedTypeSymbol serviceInterfaceSymbol;
               serviceInterfaceSymbol = ResolveServiceInterface(context.Compilation, sourceClassSymbol, options);

               if (!sourceClass.IsPartial())
               {
                  throw new TextFileGeneratorException(sourceClass, $"The class {sourceClassSymbol.Name} must be partial to participate in generation.");
               }

               ClassDeclarationSyntax targetClass;
               IEnumerable<IMethodSymbol> dummy;
               if (options.Wrapper)
                  targetClass = await generator.GenerateClientClass(context, serviceInterfaceSymbol, sourceClassSymbol.Name, sourceClassSymbol.DeclaredAccessibility, true, options.SuppressWarningComments, options.ConstructorVisibility, options.WithInternalProxy);
               else
                  targetClass = generator.GenerateProxyClass(context, serviceInterfaceSymbol, sourceClassSymbol.Name, sourceClassSymbol.DeclaredAccessibility, options.SuppressWarningComments, options.ConstructorVisibility, out dummy);

               targetClass = context.Generator.AddModifiers(targetClass, DeclarationModifiers.Partial);

               members = members.Add(CreateEnclosingMembers(context, sourceClass, targetClass));
            }
         }

         return members;
      }

      private static IReadOnlyList<MemberDeclarationSyntax> GenerateProxyInterfaces(CSharpRoslynCodeGenerationContext context, ClientProxyGenerator generator)
      {
         var sourceInterfaces = context.CompilationUnit.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
         
         ImmutableList<MemberDeclarationSyntax> members = ImmutableList<MemberDeclarationSyntax>.Empty;

         foreach (var sourceInterface in sourceInterfaces)
         {
            var sourceInterfaceSymbol = context.SemanticModel.GetDeclaredSymbol(sourceInterface);

            var generationAttribute = GetGenerationAttribute(sourceInterfaceSymbol);
            if (generationAttribute != null)
            {
               GenerationOptions options = AttributeParser.CreateInstanceFromAttribute<GenerationOptions>(generationAttribute);                  

               INamedTypeSymbol serviceInterfaceSymbol;
               serviceInterfaceSymbol = ResolveServiceInterface(context.Compilation, sourceInterfaceSymbol, options);

               if (!sourceInterface.IsPartial())
               {
                  throw new TextFileGeneratorException(sourceInterface, $"The interface {sourceInterfaceSymbol.Name} must be partial to participate in generation.");
               }

               bool implementsSourceInterface = sourceInterfaceSymbol.AllInterfaces.Any(i => i.Equals(serviceInterfaceSymbol));

               InterfaceDeclarationSyntax targetInterface = generator.GenerateProxyInterface(context.SemanticModel, context.Generator, serviceInterfaceSymbol, sourceInterfaceSymbol.Name, sourceInterfaceSymbol.DeclaredAccessibility, implementsSourceInterface, options.SuppressAsyncMethods, options.SuppressWarningComments);

               targetInterface = context.Generator.AddModifiers(targetInterface, DeclarationModifiers.Partial);

               members = members.Add(CreateEnclosingMembers(context, sourceInterface, targetInterface));
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
         var result = source.GetAttributes().Where(attr => attr.AttributeClass.Name.Equals(GenerationOptions.AttributeName)).ToImmutableArray();

         if (result.Length > 1)
            throw new TextFileGeneratorException(source, $"The {source.TypeKind} '{source.Name}' is decorated with multiple attributes of type '{GenerationOptions.AttributeName}'. Only one such attribute is allowed.");

         if (result.Length == 0)
            return null;
         
         return result[0];
      }


      private static MemberDeclarationSyntax CreateEnclosingMembers(CSharpRoslynCodeGenerationContext context, MemberDeclarationSyntax sourceMember, MemberDeclarationSyntax targetMember)
      {
         ISymbol sourceMemberSymbol = context.SemanticModel.GetDeclaredSymbol(sourceMember);
         MemberDeclarationSyntax result = targetMember;
         
         while (sourceMember.Parent is ClassDeclarationSyntax)
         {
            result = (MemberDeclarationSyntax)context.Generator.ClassDeclaration(context.Generator.GetName(sourceMember.Parent), accessibility: context.Generator.GetAccessibility(sourceMember.Parent),
               modifiers: context.Generator.GetModifiers(sourceMember.Parent).WithPartial(true), members: new[] { result });

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

      private INamedTypeSymbol GetOperationContractAttributeType(CSharpRoslynCodeGenerationContext context)
      {
         return RequireTypeSymbol(context, "System.ServiceModel.OperationContractAttribute");
      }

      #endregion

   }
}
