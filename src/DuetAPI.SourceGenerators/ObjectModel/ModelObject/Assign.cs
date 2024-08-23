using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuetAPI.SourceGenerators.ObjectModel.ModelObject
{
    /// <summary>
    /// Generate ModelObject.Assign method
    /// </summary>
    internal static class Assign
    {
        /// <summary>
        /// Generate the Assign method for a given ModelObject class
        /// </summary>
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
                        if (prop.Type is NullableTypeSyntax nts)
                        {
                            writer.WriteLine($"if (other.{prop.Identifier.ValueText} == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"if ({prop.Identifier.ValueText} == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = new();");
                            writer.Indent--;
                            writer.WriteLine("}");
                            if (receiver.DynamicModelObjectClasses.Contains(nts.ElementType.ToString()))
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = ({nts.ElementType}){prop.Identifier.ValueText}.Assign(other.{prop.Identifier.ValueText})!;");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText}.Assign(other.{prop.Identifier.ValueText});");
                            }
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else if (receiver.DynamicModelObjectClasses.Contains(propType))
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText} = ({propType}){prop.Identifier.ValueText}.Assign(other.{prop.Identifier.ValueText})!;");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.Assign(other.{prop.Identifier.ValueText});");
                        }
                    }
                    else if (propType is "ObservableCollection")
                    {
                        // Starting condition in case this value is nullable
                        if (prop.Type is NullableTypeSyntax)
                        {
                            writer.WriteLine($"if (other.{prop.Identifier.ValueText} == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"if ({prop.Identifier.ValueText} == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = new();");
                            writer.Indent--;
                            writer.WriteLine("}");
                        }

                        bool isNullableItemType = false;
                        string genericPropType = prop.GetGenericPropertyType();
                        if (genericPropType.EndsWith("?"))
                        {
                            isNullableItemType = true;
                            genericPropType = genericPropType.Substring(0, genericPropType.Length - 1);
                        }

                        // Update existing items
                        writer.WriteLine($"for (int i = 0; i < Math.Min({prop.Identifier.ValueText}.Count, other.{prop.Identifier.ValueText}.Count); i++)");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // Starting condition in case this item value is nullable
                        if (isNullableItemType)
                        {
                            writer.WriteLine($"if (other.{prop.Identifier.ValueText}[i] == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText}[i] = null;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                        }

                        // Item assignment
                        writer.WriteLine($"if ({prop.Identifier.ValueText}[i] != other.{prop.Identifier.ValueText}[i])");
                        writer.WriteLine("{");
                        writer.Indent++;
                        if (genericPropType == "DriverId")
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}[i] = (DriverId)other.{prop.Identifier.ValueText}[i].Clone();");
                        }
                        else if (genericPropType is "float[]" or "int[]")
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}[i] = other.{prop.Identifier.ValueText}[i].ToArray();");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}[i] = other.{prop.Identifier.ValueText}[i];");
                        }
                        writer.Indent--;
                        writer.WriteLine("}");

                        // Closing brace in case this item value is nullable
                        if (isNullableItemType)
                        {
                            writer.Indent--;
                            writer.WriteLine("}");
                        }

                        // End of item assignment
                        writer.Indent--;
                        writer.WriteLine("}");

                        // Add new items
                        writer.WriteLine($"for (int i = {prop.Identifier.ValueText}.Count; i < other.{prop.Identifier.ValueText}.Count; i++)");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // Starting condition in case this item value is nullable
                        if (isNullableItemType)
                        {
                            writer.WriteLine($"if (other.{prop.Identifier.ValueText}[i] == null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(null);");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                        }

                        // Add item value
                        if (genericPropType == "DriverId")
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add((DriverId)other.{prop.Identifier.ValueText}[i].Clone());");
                        }
                        else if (genericPropType is "float[]" or "int[]")
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(other.{prop.Identifier.ValueText}[i].ToArray());");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(other.{prop.Identifier.ValueText}[i]);");
                        }

                        // Closing brace in case this item value is nullable
                        if (isNullableItemType)
                        {
                            writer.Indent--;
                            writer.WriteLine("}");
                        }

                        // End of item add value
                        writer.Indent--;
                        writer.WriteLine("}");

                        // Remove excess items
                        writer.WriteLine($"while ({prop.Identifier.ValueText}.Count > other.{prop.Identifier.ValueText}.Count)");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{prop.Identifier.ValueText}.RemoveAt({prop.Identifier.ValueText}.Count - 1);");
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
                                writer.WriteLine($"if (other.{prop.Identifier.ValueText} == null)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = (DriverId)other.{prop.Identifier.ValueText}.Clone();");
                                writer.Indent--;
                                writer.WriteLine("}");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = (DriverId)other.{prop.Identifier.ValueText}.Clone();");
                            }
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText} = other.{prop.Identifier.ValueText};");
                        }
                    }
                }
                return stringWriter.ToString().TrimEnd();
            }

            // Generate method
            return SourceText.From($@"/// <summary>
        /// Assign the properties from another instance.
        /// This is required to update model properties which do not have a setter
        /// </summary>
        /// <param name=""from"">Other instance</param>{(isDynamic ? "\n        /// <returns>Updated instance</returns>" : "")}
        public {(isInherited ? "override " : isInheritedFrom ? "virtual " : "") + (isDynamic ? "IDynamicModelObject?" : "void")} Assign({(isDynamic ? "IDynamicModelObject" : "IStaticModelObject")} from)
        {{
            if (from.GetType() != GetType())
            {{
                {(isDynamic ? $"return ({cls})from.Clone();" : "throw new ArgumentNullException(nameof(from));")}
            }}

            {cls} other = ({cls})from;
            {GenerateAssignments()}{(isDynamic ? "            \nreturn this;" : "")}
        }}", Encoding.UTF8);
        }
    }
}
