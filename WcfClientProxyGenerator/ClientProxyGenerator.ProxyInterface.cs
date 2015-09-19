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
      public InterfaceDeclarationSyntax GenerateProxyInterface(INamedTypeSymbol sourceServiceInterface, string targetInterfaceName, Accessibility accessibility = Accessibility.Public, bool inheritServiceInterface = false, bool suppressAsyncMethodGeneration = false)
      {
         SyntaxGenerator gen = Context.Generator;

         SyntaxNode targetInterface = gen.InterfaceDeclaration(targetInterfaceName, accessibility: accessibility);

         targetInterface = Context.Generator.AddAttributes(targetInterface, sourceServiceInterface.GetAttributes().Select(attr => Context.Generator.Attribute(attr)));

         foreach (SyntaxNode method in GetWcfClientProxyInterfaceMethods(sourceServiceInterface, true, !inheritServiceInterface, suppressAsyncMethodGeneration))
         {
            targetInterface = Context.Generator.AddMembers(targetInterface, method);
         }

         if (inheritServiceInterface)
         {
            targetInterface = Context.Generator.AddInterfaceType(targetInterface, Context.Generator.TypeExpression(sourceServiceInterface));
         }

         targetInterface = AddGeneratedCodeAttribute(targetInterface);

         return (InterfaceDeclarationSyntax)targetInterface;
      }

   }
}
