using System;

namespace Alphaleonis
{
   [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
   public class GenerateWcfClientProxyAttribute : Attribute
   {
      private readonly string m_sourceInterfaceTypeName;
      private readonly Type m_sourceInterfaceType;

      public GenerateWcfClientProxyAttribute(string sourceInterfaceTypeName)
      {
         m_sourceInterfaceTypeName = sourceInterfaceTypeName;
      }

      public GenerateWcfClientProxyAttribute(Type sourceInterfaceType)
         : this(sourceInterfaceType.FullName)
      {
         m_sourceInterfaceType = sourceInterfaceType;
      }

      public Type SourceInterfaceType
      {
         get
         {
            return m_sourceInterfaceType;
         }
      }

      public string SourceInterfaceTypeName
      {
         get
         {
            return m_sourceInterfaceTypeName;
         }
      }

      public bool SuppressAsyncMethods { get; set; }

      public bool SuppressWarningComments { get; set; }
   }

   [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
   public class GenerateErrorHandlingWcfProxyAttribute : GenerateWcfClientProxyAttribute
   {
      private GeneratedMemberAccessibility m_constructorVisibility = GeneratedMemberAccessibility.Public;

      public GenerateErrorHandlingWcfProxyAttribute(string sourceInterfaceTypeName)
         : base(sourceInterfaceTypeName)
      {
      }

      public GenerateErrorHandlingWcfProxyAttribute(Type sourceInterfaceType)
         : base(sourceInterfaceType)
      {
      }

      public GeneratedMemberAccessibility ConstructorVisibility
      {
         get
         {
            return m_constructorVisibility;
         }

         set
         {
            m_constructorVisibility = value;
         }
      }
   }

   [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
   public class GenerateErrorHandlingWcfProxyWrapperAttribute : GenerateErrorHandlingWcfProxyAttribute
   {
      public GenerateErrorHandlingWcfProxyWrapperAttribute(string sourceInterfaceTypeName)
         : base(sourceInterfaceTypeName)
      {
      }

      public GenerateErrorHandlingWcfProxyWrapperAttribute(Type sourceInterfaceType)
         : base(sourceInterfaceType)
      {
      }      
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