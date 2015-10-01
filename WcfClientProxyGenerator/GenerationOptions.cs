using Alphaleonis.Vsx;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using System.Reflection;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public class GenerationOptions 
   {
      public const string AttributeName = "GenerateWcfClientCodeAttribute";

      public GenerationOptions(INamedTypeSymbol sourceInterfaceType)
      {
         SourceInterfaceType = sourceInterfaceType;
         SourceInterfaceTypeName = sourceInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
         ConstructorVisibility = MemberAccessibility.Public;
      }

      public GenerationOptions(string sourceInterfaceTypeName)
      {
         SourceInterfaceTypeName = sourceInterfaceTypeName;
         ConstructorVisibility = MemberAccessibility.Public;
      }

      public INamedTypeSymbol SourceInterfaceType { get; }

      public string SourceInterfaceTypeName { get; }

      public bool SuppressAsyncMethods { get; set; }

      public bool SuppressWarningComments { get; set; }

      public bool Wrapper { get; set; }

      public bool WithInternalProxy { get; set; }

      public MemberAccessibility ConstructorVisibility { get; set; }
   }

   public enum MemberAccessibility
   {
      Public,
      Protected,
      Internal,
      Private,
      ProtectedInternal
   }
}
