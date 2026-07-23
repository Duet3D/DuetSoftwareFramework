using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuetAPI.SourceGenerators.ObjectModel.ModelObject;

internal static class PropertyAccess
{
    /// <summary>
    /// Function to generate the reflection-free property accessors
    /// </summary>
    /// <param name="receiver">Syntax receiver</param>
    /// <param name="cls">Class name</param>
    /// <returns>Generated members</returns>
    public static SourceText Generate(SourceGeneratorSyntaxReceiver receiver, string cls)
    {
        List<PropertyDeclarationSyntax> properties = receiver.ModelObjectMembers[cls];
        bool isInherited = receiver.InheritedClasses.Any(ic => ic.Key.Identifier.ValueText == cls), isInheritedFrom = receiver.InheritedClasses.Any(ic => ic.Value == cls);
        string modifier = isInherited ? "override " : isInheritedFrom ? "virtual " : "";

        bool IsModelObject(string type) => receiver.ModelObjectMembers.ContainsKey(type);
        bool IsModelCollection(string type) => type is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" || receiver.ModelCollectionMembers.ContainsKey(type);
        bool IsModelDictionary(string type) => type is "JsonModelDictionary" or "StaticModelDictionary";

        string GetKind(PropertyDeclarationSyntax prop)
        {
            string propType = prop.GetPropertyType();
            if (IsModelObject(propType))
            {
                return "ModelObject";
            }
            if (IsModelCollection(propType))
            {
                return "ModelCollection";
            }
            if (IsModelDictionary(propType))
            {
                return "ModelDictionary";
            }
            if (propType == "ObservableCollection")
            {
                return "ObservableCollection";
            }
            if (propType == "JsonElement")
            {
                return "JsonElement";
            }
            return "Value";
        }

        // Resolve the nested model type whose descriptor describes the items of this property
        string? GetElementType(PropertyDeclarationSyntax prop)
        {
            string propType = prop.GetPropertyType();
            if (IsModelObject(propType))
            {
                return propType;
            }
            if (receiver.ModelCollectionItemTypes.TryGetValue(propType, out string itemType))
            {
                return itemType;
            }
            if (IsModelCollection(propType) || IsModelDictionary(propType))
            {
                TypeSyntax type = (prop.Type is NullableTypeSyntax nts) ? nts.ElementType : prop.Type;
                if (type is GenericNameSyntax gns)
                {
                    return gns.TypeArgumentList.Arguments[gns.TypeArgumentList.Arguments.Count - 1].ToString().TrimEnd('?');
                }
            }
            return null;
        }

        string GetFlags(PropertyDeclarationSyntax prop)
        {
            List<string> flags = [];
            if (prop.HasSetter())
            {
                flags.Add("ModelPropertyFlags.HasSetter");
            }
            if (prop.IsSbcProperty())
            {
                flags.Add("ModelPropertyFlags.SbcProperty");
            }
            if (prop.IsLiveProperty())
            {
                flags.Add("ModelPropertyFlags.Live");
            }
            if (prop.IsVerboseProperty())
            {
                flags.Add("ModelPropertyFlags.Verbose");
            }
            if (prop.IsObsoleteProperty())
            {
                flags.Add("ModelPropertyFlags.Obsolete");
            }
            return (flags.Count != 0) ? string.Join(" | ", flags) : "ModelPropertyFlags.None";
        }

        string GenerateDescriptors()
        {
            using StringWriter stringWriter = new();
            using IndentedTextWriter writer = new(stringWriter)
            {
                Indent = 2
            };

            for (int i = 0; i < properties.Count; i++)
            {
                PropertyDeclarationSyntax prop = properties[i];
                string? elementType = GetElementType(prop);
                // The type has to be qualified because a property may share its name with the type it holds, e.g. Job.Layer
                string elementDescriptor = (elementType is not null && IsModelObject(elementType)) ? $", static () => global::DuetAPI.ObjectModel.{elementType}.TypeDescriptor" : "";
                string separator = (i < properties.Count - 1) ? "," : "";
                writer.WriteLine($"new ModelPropertyDescriptor({i}, \"{prop.Identifier.ValueText}\", \"{prop.GetJsonPropertyName()}\", ModelPropertyKind.{GetKind(prop)}, {GetFlags(prop)}{elementDescriptor}){separator}");
            }
            return stringWriter.ToString().TrimEnd();
        }

        string GenerateValueCases()
        {
            using StringWriter stringWriter = new();
            using IndentedTextWriter writer = new(stringWriter)
            {
                Indent = 3
            };

            for (int i = 0; i < properties.Count; i++)
            {
                writer.WriteLine($"{i} => {properties[i].Identifier.ValueText},");
            }
            return stringWriter.ToString().TrimEnd();
        }

        return SourceText.From($@"/// <summary>
    /// Static description of this type
    /// </summary>
    /// <remarks>This property is auto-generated</remarks>
    public static {(isInherited ? "new " : "")}IModelObjectDescriptor TypeDescriptor {{ get; }} = new ModelObjectDescriptor(
{GenerateDescriptors()});

    /// <summary>
    /// Static description of this instance's type
    /// </summary>
    /// <remarks>This property is auto-generated</remarks>
    [JsonIgnore]
    public {modifier}IModelObjectDescriptor Descriptor => TypeDescriptor;

    /// <summary>
    /// Read the value of the property with the given index
    /// </summary>
    /// <param name=""index"">Index of the property</param>
    /// <returns>Property value</returns>
    /// <exception cref=""ArgumentOutOfRangeException"">Property index is invalid</exception>
    /// <remarks>This method is auto-generated</remarks>
    public {modifier}object? GetPropertyValue(int index)
    {{
        return index switch
        {{
{GenerateValueCases()}
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        }};
    }}", Encoding.UTF8);
    }
}
