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
      public InterfaceDeclarationSyntax GenerateProxyInterface(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol sourceServiceInterface, string targetInterfaceName, Accessibility accessibility = Accessibility.Public, bool inheritServiceInterface = false, bool suppressAsyncMethodGeneration = false)
      {
         SyntaxNode targetInterface = context.Generator.InterfaceDeclaration(targetInterfaceName, accessibility: accessibility);

         targetInterface = context.Generator.AddAttributes(targetInterface, sourceServiceInterface.GetAttributes().Select(attr => context.Generator.Attribute(attr)));

         foreach (SyntaxNode method in GetWcfClientProxyInterfaceMethods(context, sourceServiceInterface, true, !inheritServiceInterface, suppressAsyncMethodGeneration))
         {
            targetInterface = context.Generator.AddMembers(targetInterface, method);
         }

         if (inheritServiceInterface)
         {
            targetInterface = context.Generator.AddInterfaceType(targetInterface, context.Generator.TypeExpression(sourceServiceInterface));
         }

         targetInterface = AddGeneratedCodeAttribute(context.Generator, targetInterface);

         return (InterfaceDeclarationSyntax)targetInterface;
      }

   }
}
