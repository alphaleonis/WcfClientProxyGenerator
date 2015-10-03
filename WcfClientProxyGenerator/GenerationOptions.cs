using Alphaleonis.Vsx;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using System.Reflection;

namespace Alphaleonis.WcfClientProxyGenerator
{
   internal class ProxyGenerationOptions 
   {
      public const string GenerateProxyAttributeName = "GenerateWcfClientProxyAttribute";
      public const string GenerateErrorHandlingProxyAttributeName = "GenerateErrorHandlingWcfProxyAttribute";
      public const string GenerateErrorHandlingProxyWrapperAttributeName = "GenerateErrorHandlingWcfProxyWrapperAttribute";

      public ProxyGenerationOptions(INamedTypeSymbol sourceInterfaceType)
      {
         SourceInterfaceType = sourceInterfaceType;
         SourceInterfaceTypeName = sourceInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      }

      public ProxyGenerationOptions(string sourceInterfaceTypeName)
      {
         SourceInterfaceTypeName = sourceInterfaceTypeName;
      }

      public INamedTypeSymbol SourceInterfaceType { get; } 

      public string SourceInterfaceTypeName { get; }

      public bool SuppressAsyncMethods { get; set; }

      public bool SuppressWarningComments { get; set; }

      public string AttributeName { get; set; }

      public MemberAccessibility ConstructorVisibility { get; set; } = MemberAccessibility.Public;
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
