using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SourceGenerators.ObjectModel
{
    internal static class Helpers
    {
        public static string GetJsonPropertyName(PropertyDeclarationSyntax pds)
        {
            string name = pds.Identifier.ValueText;
            if (name == "SBC")
            {
                return "sbc";
            }
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        public static bool IsSettable(PropertyDeclarationSyntax pds)
        {
            return pds.AccessorList?.Accessors.Any(a => a.Keyword.ValueText == "set") ?? false;
        }

        public static bool IsSbcProperty(PropertyDeclarationSyntax pds)
        {
            return pds.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "SbcProperty"));
        }
    }
}
