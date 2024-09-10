using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;

namespace DuetAPI.SourceGenerators
{
    internal static class Helpers
    {
        public static string GetJsonPropertyName(this PropertyDeclarationSyntax propertySyntax)
        {
            string name = propertySyntax.Identifier.ValueText;
            return name switch
            {
                "gCodes" => "gcodes",
                "PID" => "pid",
                "SBC" => "sbc",
                _ => char.ToLowerInvariant(name[0]) + name.Substring(1)
            };
        }

        public static string GetPropertyType(this PropertyDeclarationSyntax propertySyntax)
        {
            if (propertySyntax.Type is NullableTypeSyntax nts)
            {
                if (nts.ElementType is GenericNameSyntax gns)
                {
                    return gns.Identifier.ValueText;
                }
                return nts.ElementType.ToString();
            }
            else if (propertySyntax.Type is GenericNameSyntax gns)
            {
                return gns.Identifier.ValueText;
            }
            return propertySyntax.Type.ToString();
        }

        public static string GetGenericPropertyType(this PropertyDeclarationSyntax propertySyntax)
        {
            if (propertySyntax.Type is NullableTypeSyntax nts)
            {
                if (nts.ElementType is GenericNameSyntax gns)
                {
                    return gns.TypeArgumentList.Arguments[0].ToString();
                }
                return nts.ElementType.ToString();
            }
            else if (propertySyntax.Type is GenericNameSyntax gns)
            {
                return gns.TypeArgumentList.Arguments[0].ToString();
            }
            throw new ArgumentException("Property is not a generic type");
        }

        public static bool HasSetter(this PropertyDeclarationSyntax propertySyntax)
        {
            return propertySyntax.AccessorList != null && propertySyntax.AccessorList.Accessors.Any(SyntaxKind.SetAccessorDeclaration);
        }

        public static bool IsSbcProperty(this PropertyDeclarationSyntax propertySyntax)
        {
            return propertySyntax.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "SbcProperty"));
        }
    }
}
