using System;

namespace Alphaleonis
{
   [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
   public class GenerateWcfClientCodeAttribute : Attribute
   {
      public GenerateWcfClientCodeAttribute(string sourceInterfaceTypeName)
      {
         SourceInterfaceTypeName = sourceInterfaceTypeName;
         ConstructorVisibility = GeneratedMemberAccessibility.Public;
      }

      public GenerateWcfClientCodeAttribute(Type sourceInterfaceType)
      {
         SourceInterfaceType = sourceInterfaceType;
         SourceInterfaceTypeName = sourceInterfaceType.FullName;
         ConstructorVisibility = GeneratedMemberAccessibility.Public;
      }

      public Type SourceInterfaceType
      {
         get; private set;
      }

      public string SourceInterfaceTypeName
      {
         get; private set;
      }

      public bool SuppressAsyncMethods { get; set; }

      public bool SuppressWarningComments { get; set; }

      public bool Wrapper { get; set; }

      public GeneratedMemberAccessibility ConstructorVisibility { get; set; }
   }

   public enum GeneratedMemberAccessibility
   {
      Public,
      Protected,
      Internal,
      Private,
      ProtectedInternal
   }
}