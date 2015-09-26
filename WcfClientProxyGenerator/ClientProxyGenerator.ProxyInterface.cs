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
      public InterfaceDeclarationSyntax GenerateProxyInterface(SemanticModel semanticModel, SyntaxGenerator generator, INamedTypeSymbol sourceServiceInterface, string targetInterfaceName, Accessibility accessibility = Accessibility.Public, bool inheritServiceInterface = false, bool suppressAsyncMethodGeneration = false, bool suppressWarningComments = false)
      {
         SyntaxNode targetInterface = generator.InterfaceDeclaration(targetInterfaceName, accessibility: accessibility);

         targetInterface = generator.AddAttributes(targetInterface, sourceServiceInterface.GetAttributes().Select(attr => generator.Attribute(attr)));

         foreach (SyntaxNode method in GetOperationContractMethodDeclarations(semanticModel, generator, sourceServiceInterface, true, !inheritServiceInterface, suppressAsyncMethodGeneration))
         {
            targetInterface = generator.AddMembers(targetInterface, generator.AddWarningCommentIf(!suppressWarningComments, method));
         }

         if (inheritServiceInterface)
         {
            targetInterface = generator.AddInterfaceType(targetInterface, generator.TypeExpression(sourceServiceInterface));
         }

         targetInterface = AddGeneratedCodeAttribute(generator, targetInterface);

         targetInterface = generator.AddWarningCommentIf(!suppressWarningComments, targetInterface);

         return (InterfaceDeclarationSyntax)targetInterface;
      }

   }
}
