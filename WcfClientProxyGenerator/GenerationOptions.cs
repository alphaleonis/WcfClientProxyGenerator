using Alphaleonis.Vsx;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public class GenerationOptions
   {
      public GenerationOptions(AttributeData attribute)
      {
         if (attribute == null)
            throw new ArgumentNullException(nameof(attribute), $"{nameof(attribute)} is null.");

         foreach (var property in GetType().GetProperties())
         {
            KeyValuePair<string, TypedConstant> argumentEntry = attribute.NamedArguments.FirstOrDefault(arg => arg.Key.Equals(property.Name));
            if (argumentEntry.Key != null)
            {
               TypedConstant argument = argumentEntry.Value;
               if (argument.Value == null && property.PropertyType.IsValueType)
               {
                  throw new TextFileGeneratorException($"The argument {argumentEntry.Key} of attribute {attribute.AttributeClass.Name} cannot be null, since it should be a value type ({property.PropertyType.Name}).");
               }
               else
               {
                  if (!property.PropertyType.IsAssignableFrom(argument.Value.GetType()))
                     throw new TextFileGeneratorException(attribute.ApplicationSyntaxReference.GetSyntax(), $"The argument {argumentEntry.Key} of attribute {attribute.AttributeClass.Name} has the wrong type. Expected {property.PropertyType.Name} but was {argument.Value.GetType()}.");
               }

               property.SetValue(this, argument.Value);
            }
         }
      }

      public string SourceInterfaceTypeName { get; set; }

      public INamedTypeSymbol SourceInterfaceType { get; set; }

      public bool SuppressAsyncMethods { get; set; }      
   }
}
