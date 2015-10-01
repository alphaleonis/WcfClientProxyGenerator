using System;
using System.Collections.Generic;
using System.Linq;

namespace Alphaleonis.WcfClientProxyGenerator
{
   internal class GenerationNameTable
   {
      private class Scope
      {
         private readonly Scope m_parentScope;
         private readonly Dictionary<string, string> m_nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
         private readonly HashSet<string> m_reservedNames;

         public Scope(Scope parentScope, IEnumerable<string> reservedNames)
         {
            m_parentScope = parentScope;
            m_reservedNames = new HashSet<string>((reservedNames ?? Enumerable.Empty<string>()), StringComparer.OrdinalIgnoreCase);
         }

         public void Reserve(string name)
         {
            m_reservedNames.Add(name);
         }

         public string GetName(string desiredName)
         {
            string result = TryGetExistingName(desiredName);
            if (result == null)
            {
               result = CreateUniqueName(desiredName);
               m_reservedNames.Add(result);
               m_nameMap.Add(desiredName, result);
            }

            return result;
         }

         public Scope Parent
         {
            get
            {
               return m_parentScope;
            }
         }

         private bool IsReserved(string name)
         {
            return m_reservedNames.Contains(name) || m_parentScope != null && m_parentScope.IsReserved(name);
         }

         private string TryGetExistingName(string name)
         {
            string result;
            if (!m_nameMap.TryGetValue(name, out result))
            {
               result = m_parentScope?.TryGetExistingName(name);
            }

            return result;
         }

         private string CreateUniqueName(string desiredName)
         {
            var result = desiredName;
            int i = 0;
            while (IsReserved(result))
            {
               result = $"{desiredName}_{i++}";
            }

            return result;
         }
      }

      private Scope m_currentScope;

      public GenerationNameTable(IEnumerable<string> reservedNames)
      {
         m_currentScope = new Scope(null, reservedNames);
      }

      public string this[string desiredName]
      {
         get
         {
            return m_currentScope.GetName(desiredName);
         }
      }

      public IDisposable PushScope(IEnumerable<string> additionallyReservedNames)
      {
         m_currentScope = new Scope(m_currentScope, additionallyReservedNames);
         return new ScopeDisposer(this);
      }

      private class ScopeDisposer : IDisposable
      {
         private GenerationNameTable m_table;

         public ScopeDisposer(GenerationNameTable table)
         {
            m_table = table;
         }

         public void Dispose()
         {
            m_table.m_currentScope = m_table.m_currentScope.Parent ?? m_table.m_currentScope;
         }
      }
   }
}

