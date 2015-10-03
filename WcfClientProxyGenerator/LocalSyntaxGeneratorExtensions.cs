using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editing;
using Alphaleonis.Vsx.Roslyn.CSharp;

namespace Alphaleonis.WcfClientProxyGenerator
{
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

