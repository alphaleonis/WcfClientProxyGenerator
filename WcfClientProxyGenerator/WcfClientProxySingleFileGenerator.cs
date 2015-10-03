using Alphaleonis.Vsx;
using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AlphaVSX.Roslyn;
using System.Linq;
using System.Collections.Immutable;
using System.Threading;
using Alphaleonis.Vsx.IDE;
using System.IO;

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


      protected override Task<CompilationUnitSyntax> GenerateCompilationUnit(Document sourceDocument)
      {
         EnvDTE.ProjectItem projItem = GetService(typeof(EnvDTE.ProjectItem)) as EnvDTE.ProjectItem;

         // Remove any previously generated documents first, since we may otherwise get those as well during 
         // parsing, leading to duplicate method generations.
         foreach (EnvDTE.ProjectItem item in projItem.ProjectItems)
         {
            if (item.FileCount > 0 && item.FileNames[0].EndsWith(GetDefaultExtension()))
            {
               var docToRemove = sourceDocument.Project.Documents.FirstOrDefault(doc => doc.FilePath == item.FileNames[0]);
               if (docToRemove != null)
               {
                  sourceDocument = sourceDocument.Project.RemoveDocument(docToRemove.Id).GetDocument(sourceDocument.Id);
               }
            }
         }
                    
         return ClientProxyGenerator.Generate(sourceDocument, CancellationToken.None);
      }

      protected override string GetDefaultExtension()
      {
         return ".g.cs";
      }


   }
}
