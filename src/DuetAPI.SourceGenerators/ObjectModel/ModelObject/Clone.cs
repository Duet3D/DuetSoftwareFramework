using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuetAPI.SourceGenerators.ObjectModel.ModelObject
{
    /// <summary>
    /// Generate ModelObject.Clone method
    /// </summary>
    internal static class Clone
    {
        /// <summary>
        /// Generate the Assign method for a given ModelObject class
        /// </summary>
        /// <param name="context">Generator context</param>
        /// <param name="receiver">Syntax receiver</param>
        /// <param name="cls">Class name</param>
        /// <returns>Generated method</returns>
        public static SourceText Generate(SourceGeneratorSyntaxReceiver receiver, string cls)
        {
            List<PropertyDeclarationSyntax> properties = receiver.ModelObjectMembers[cls];
            List<MethodDeclarationSyntax> methods = receiver.ModelObjectMethods[cls];
            bool isDynamic = receiver.DynamicModelObjectClasses.Contains(cls);
            bool isInherited = receiver.InheritedClasses.Any(ic => ic.Key.Identifier.ValueText == cls), isInheritedFrom = receiver.InheritedClasses.Any(ic => ic.Value == cls);

            string GenerateAssignments()
            {
                using StringWriter stringWriter = new();
                using IndentedTextWriter writer = new(stringWriter)
                {
                    Indent = 3
                };

                foreach (var prop in properties)
                {
                    string jsonPropertyName = prop.GetJsonPropertyName(), propType = prop.GetPropertyType();

                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "GrowingCollection" or "JsonModelDictionary" or "StaticModelDictionary" ||
                        receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                    {
                        bool isNullable = prop.Type is NullableTypeSyntax nts;
                        if (receiver.DynamicModelObjectClasses.Contains(propType) || isNullable)
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText} = ({prop.Type}){prop.Identifier.ValueText}{(isNullable ? "?" : "")}.Clone();");
                        }
                        else
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText}.Assign({prop.Identifier.ValueText});");
                        }
                    }
                    else if (propType is "ObservableCollection")
                    {
                        string genericPropType = prop.GetGenericPropertyType();
                        bool isNullableItemType = genericPropType.EndsWith("?");

                        // Starting condition in case this value is nullable
                        if (prop.Type is NullableTypeSyntax)
                        {
                            writer.WriteLine($"if ({prop.Identifier.ValueText} == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"clone.{prop.Identifier.ValueText} = null;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"if (clone.{prop.Identifier.ValueText} == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"clone.{prop.Identifier.ValueText} = new ObservableCollection<{genericPropType}>();");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"clone.{prop.Identifier.ValueText}.Clear();");
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText}.Clear();");
                        }

                        // Add cloned items
                        writer.WriteLine($"foreach ({genericPropType} item in {prop.Identifier.ValueText})");
                        writer.WriteLine("{");
                        writer.Indent++;
                        if (genericPropType == "DriverId")
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText}.Add((DriverId)item{(isNullableItemType ? "?" : "")}.Clone());");
                        }
                        else if (genericPropType is "float[]" or "int[]")
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText}.Add(item{(isNullableItemType ? "?" : "")}.ToArray());");
                        }
                        else
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText}.Add(item);");
                        }

                        // End of add cloned items
                        writer.Indent--;
                        writer.WriteLine("}");

                        // Closing brace in case this value is nullable
                        if (prop.Type is NullableTypeSyntax)
                        {
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                    }
                    else
                    {
                        // assignment
                        if (propType is "DriverId")
                        {
                            if (prop.Type is NullableTypeSyntax)
                            {
                                writer.WriteLine($"if ({prop.Identifier.ValueText} == null)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"clone.{prop.Identifier.ValueText} = null;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"clone.{prop.Identifier.ValueText} = (DriverId){prop.Identifier.ValueText}.Clone();");
                                writer.Indent--;
                                writer.WriteLine("}");
                            }
                            else
                            {
                                writer.WriteLine($"clone.{prop.Identifier.ValueText} = (DriverId){prop.Identifier.ValueText}.Clone();");
                            }
                        }
                        else
                        {
                            writer.WriteLine($"clone.{prop.Identifier.ValueText} = {prop.Identifier.ValueText};");
                        }
                    }
                }
                return stringWriter.ToString().TrimEnd();
            }

            // Generate method
            return SourceText.From($@"/// <summary>
        /// Clone this instance
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <returns>Cloned instance</returns>
        public {(isInherited ? "override " : isInheritedFrom ? "virtual " : "")}object Clone()
        {{
            {cls} clone = new();
            {GenerateAssignments()}
            return clone;
        }}", Encoding.UTF8);
        }
    }
}
