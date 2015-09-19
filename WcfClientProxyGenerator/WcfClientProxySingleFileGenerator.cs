using Alphaleonis.Vsx;
using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AlphaVSX.Roslyn;
using System.Linq;
using System.Collections.Immutable;

namespace Alphaleonis.WcfClientProxyGenerator
{

   [ComVisible(true)]
   [Guid("E6E9A8E8-E940-476E-9F8B-2A554BD3AD3A")]
   [Microsoft.VisualStudio.Shell.CodeGeneratorRegistration(typeof(WcfClientProxySingleFileGenerator), "C# WCF Client Proxy Generator", VSLangProj80.vsContextGuids.vsContextGuidVCSProject, GeneratesDesignTimeSource = true, GeneratorRegKeyName = Name)]
   [Microsoft.VisualStudio.Shell.ProvideObject(typeof(WcfClientProxySingleFileGenerator))]
   public class WcfClientProxySingleFileGenerator : CSharpRoslynCodeGenerator
   {
#pragma warning disable 0414
      //The name of this generator (use for 'Custom Tool' property of project item)
      internal const string Name = "WcfClientProxyGenerator";
#pragma warning restore 0414

      

      public WcfClientProxySingleFileGenerator()
      {
      }


      protected override Task<CompilationUnitSyntax> GenerateCompilationUnit(CSharpRoslynCodeGenerationContext context)
      {
         return ClientProxyGenerator.Generate(context);
      }

      protected override string GetDefaultExtension()
      {
         return ".g.cs";
      }


   }
}
