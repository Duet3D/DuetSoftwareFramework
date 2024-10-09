using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    /// Generate ModelOBject.UpdateFromJson method
    /// </summary>
    internal static class UpdateFromJson
    {
        /// <summary>
        /// Generate the UpdateFromJson method for a given ModelObject class
        /// </summary>
        /// <param name="context">Generator context</param>
        /// <param name="receiver">Syntax receiver</param>
        /// <param name="cls">Class name</param>
        /// <returns>Generated method</returns>
        public static SourceText Generate(GeneratorExecutionContext context, SourceGeneratorSyntaxReceiver receiver, string cls)
        {
            List<PropertyDeclarationSyntax> properties = receiver.ModelObjectMembers[cls];
            List<MethodDeclarationSyntax> methods = receiver.ModelObjectMethods[cls];
            bool isDynamic = receiver.DynamicModelObjectClasses.Contains(cls);
            bool isInherited = receiver.InheritedClasses.Any(ic => ic.Key.Identifier.ValueText == cls), isInheritedFrom = receiver.InheritedClasses.Any(ic => ic.Value == cls);

            string GeneratePropertyUpdateCalls()
            {
                using StringWriter stringWriter = new();
                using IndentedTextWriter writer = new(stringWriter)
                {
                    Indent = 4
                };

                bool first = true;
                foreach (var prop in properties)
                {
                    string jsonPropertyName = prop.GetJsonPropertyName(), propType = prop.GetPropertyType();

                    // (else) if (key == <propName>) {
                    writer.WriteLine($"{(first ? "if" : "else if")} (jsonProperty.Name == \"{jsonPropertyName}\")");
                    writer.WriteLine("{");
                    writer.Indent++;
                    first = false;

                    // SBC property check
                    bool isSbcProperty = prop.IsSbcProperty();
                    if (isSbcProperty)
                    {
                        writer.WriteLine("if (!ignoreSbcProperties)");
                        writer.WriteLine("{");
                        writer.Indent++;
                    }

                    // assignment
                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" or "JsonModelDictionary" or "StaticModelDictionary" ||
                        receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                    {
                        if (prop.Type is NullableTypeSyntax nts)
                        {
                            writer.WriteLine("if (jsonProperty.Value.ValueKind == JsonValueKind.Null)");
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
                            writer.WriteLine($"{prop.Identifier.ValueText} = new {nts.ElementType}();");
                            writer.Indent--;
                            writer.WriteLine("}");
                            if (receiver.DynamicModelObjectClasses.Contains(nts.ElementType.ToString()))
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = ({nts.ElementType}){prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);");
                            }
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else if (receiver.DynamicModelObjectClasses.Contains(propType))
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText} = ({propType}){prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties)!;");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);");
                        }
                    }
                    else if (propType is "ObservableCollection")
                    {
                        // Starting condition in case this value is nullable
                        if (prop.Type is NullableTypeSyntax)
                        {
                            writer.WriteLine("if (jsonProperty.Value.ValueKind == JsonValueKind.Null)");
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
                            writer.WriteLine();
                        }

                        bool isNullableItemType = false;
                        string genericPropType = prop.GetGenericPropertyType();
                        if (genericPropType.EndsWith("?"))
                        {
                            isNullableItemType = true;
                            genericPropType = genericPropType.Substring(0, genericPropType.Length - 1);
                        }

                        bool isEnum = false;
                        Tuple<string, string>? varNameAndItemGetter = genericPropType switch
                        {
                            "int" => new("newIntValue", "GetInt32()"),
                            "string" => new("newStringValue", "GetString()!"),
                            "char" => new("newCharValue", "GetString()![0]"),
                            "float" => new("newFloatValue", "GetSingle()"),
                            "float[]" => new("newFloatArrayValue", "EnumerateArray().Select(e => e.GetSingle()).ToArray()"),
                            "int[]" => new("newIntArrayValue", "EnumerateArray().Select(e => e.GetInt32()).ToArray()"),
                            "DriverId" => new("newDriverIdValue", "GetString()!"),
                            _ => null
                        };
                        if (varNameAndItemGetter == null && receiver.Enums.Contains(genericPropType))
                        {
                            isEnum = true;
                            varNameAndItemGetter = new($"new{genericPropType}Value", "GetString()!");
                        }
                        if (varNameAndItemGetter == null)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedType, prop.GetLocation(), jsonPropertyName, cls));
                            continue;
                        }

                        // try
                        writer.WriteLine("try");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // Update existing items
                        writer.WriteLine("int newCount = jsonProperty.Value.GetArrayLength();");
                        writer.WriteLine($"for (int i = 0; i < Math.Min({prop.Identifier.ValueText}.Count, newCount); i++)");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // Starting condition in case this item value is nullable
                        if (isNullableItemType)
                        {
                            writer.WriteLine("if (jsonProperty.Value[i].ValueKind == JsonValueKind.Null)");
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
                        if (genericPropType == "DriverId")
                        {
                            writer.WriteLine("DriverId newDriverIdValue = new DriverId(jsonProperty.Value[i].GetString()!);");
                        }
                        else if (isEnum)
                        {
                            writer.WriteLine($"{genericPropType} new{genericPropType}Value = JsonSerializer.Deserialize(jsonProperty.Value[i].GetRawText(), ObjectModelContext.Default.{genericPropType})!;");
                        }
                        else
                        {
                            writer.WriteLine($"{genericPropType} {varNameAndItemGetter!.Item1} = jsonProperty.Value[i].{varNameAndItemGetter.Item2};");
                        }
                        writer.WriteLine($"if ({prop.Identifier.ValueText}[i] != {varNameAndItemGetter.Item1})");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{prop.Identifier.ValueText}[i] = {varNameAndItemGetter.Item1};");
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
                        writer.WriteLine($"for (int i = {prop.Identifier.ValueText}.Count; i < newCount; i++)");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // Starting condition in case this item value is nullable
                        if (isNullableItemType)
                        {
                            writer.WriteLine("if (jsonProperty.Value[i].ValueKind == JsonValueKind.Null)");
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
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(new DriverId(jsonProperty.Value[i].GetString()!));");
                        }
                        else if (isEnum)
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(JsonSerializer.Deserialize(jsonProperty.Value[i].GetRawText(), ObjectModelContext.Default.{genericPropType})!);");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(jsonProperty.Value[i].{varNameAndItemGetter.Item2});");
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
                        writer.WriteLine($"while ({prop.Identifier.ValueText}.Count > newCount)");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{prop.Identifier.ValueText}.RemoveAt({prop.Identifier.ValueText}.Count - 1);");
                        writer.Indent--;
                        writer.WriteLine("}");

                        // catch
                        writer.Indent--;
                        writer.WriteLine("}");
                        writer.WriteLine($"catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof({prop.Type.ToString().TrimEnd('?')}), jsonProperty.Value.Clone(), e))");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine("// suppressed");
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
                        // try
                        writer.WriteLine("try");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // assignment
                        if (propType is "DriverId")
                        {
                            if (prop.Type is NullableTypeSyntax)
                            {
                                writer.WriteLine("if (jsonProperty.Value.ValueKind == JsonValueKind.Null)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = new(jsonProperty.Value.GetString()!);");
                                writer.Indent--;
                                writer.WriteLine("}");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = new(jsonProperty.Value.GetString()!);");
                            }
                        }
                        else
                        {
                            string? getter = propType switch
                            {
                                "string" => "GetString()!",
                                "char" => "GetString()![0]",
                                "int" => "GetInt32()",
                                "bool" => "GetBoolean()",
                                "double" => "GetDouble()",
                                "float" => "GetSingle()",
                                "long" => "GetInt64()",
                                "ulong" => "GetUInt64()",
                                "uint" => "GetUInt32()",
                                "short" => "GetInt16()",
                                "ushort" => "GetUInt16()",
                                "byte" => "GetByte()",
                                "sbyte" => "GetSByte()",
                                "decimal" => "GetDecimal()",
                                "DateTime" => "GetDateTime()",
                                "DateTimeOffset" => "GetDateTimeOffset()",
                                "TimeSpan" => "GetTimeSpan()",
                                _ => null
                            };

                            if (getter != null)
                            {
                                if (prop.Type is NullableTypeSyntax)
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = (jsonProperty.Value.ValueKind == JsonValueKind.Null) ? null : jsonProperty.Value.{getter};");
                                }
                                else
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.{getter};");
                                }
                            }
                            else if (receiver.Enums.Contains(propType))
                            {
                                if (prop.Type is NullableTypeSyntax)
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = (jsonProperty.Value.ValueKind == JsonValueKind.Null) ? null : JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), ObjectModelContext.Default.{propType})!;");
                                }
                                else
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), ObjectModelContext.Default.{propType})!;");
                                }
                            }
                            else if (propType is "object")
                            {
                                writer.WriteLine("if (jsonProperty.Value.ValueKind == JsonValueKind.Null)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else if (jsonProperty.Value.ValueKind == JsonValueKind.String)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetString()!;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else if (jsonProperty.Value.ValueKind == JsonValueKind.Number)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine("if (jsonProperty.Value.TryGetInt32(out int intValue))");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = intValue;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetSingle();");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetRawText();");
                                writer.WriteLineNoTabs("#if VERIFY_OBJECT_MODEL");
                                writer.WriteLine($"Console.WriteLine($\"[warn] Unsupported object type {{jsonProperty.Value.ValueKind}} for property {jsonPropertyName} in {cls}\");");
                                writer.WriteLineNoTabs("#endif");
                                writer.Indent--;
                                writer.WriteLine("}");
                            }
                            else
                            {
                                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedType, prop.GetLocation(), jsonPropertyName, cls));
                            }
                        }

                        // catch
                        writer.Indent--;
                        writer.WriteLine("}");
                        writer.WriteLine($"catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof({prop.Type.ToString().TrimEnd('?')}), jsonProperty.Value.Clone(), e))");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine("// suppressed");
                        writer.Indent--;
                        writer.WriteLine("}");
                    }

                    if (isSbcProperty)
                    {
                        writer.Indent--;
                        writer.WriteLine("}");
                    }

                    // }
                    writer.Indent--;
                    writer.WriteLine("}");
                }
                return stringWriter.ToString().TrimEnd();
            }

            // Check if we need to generate the UpdateFromJson(Reader) methods
            bool useGeneratedUpdateFromJson = methods.Any(mds => mds.Identifier.ValueText == "UpdateFromJson" && mds.ParameterList.Parameters.Count == 2 && mds.ParameterList.Parameters[0].Identifier.ValueText == "jsonElement" && mds.ParameterList.Parameters[1].Identifier.ValueText == "ignoreSbcProperties");

            // Generate method
            return SourceText.From($@"/// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""jsonElement"">Element to update this intance from</param>
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>{(isDynamic ? "\n        /// <returns>Updated instance</returns>" : "")}
        /// <exception cref=""JsonException"">Failed to deserialize data</exception>
        public {(isInherited ? "override " : isInheritedFrom ? "virtual " : "") + (isDynamic ? "IDynamicModelObject?" : "void")} {(useGeneratedUpdateFromJson ? "Generated" : "")}UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {{
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {{
                {(isDynamic ? "return null;" : "throw new ArgumentNullException(nameof(jsonElement));")}
            }}

            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {{
                {GeneratePropertyUpdateCalls()}
#if VERIFY_OBJECT_MODEL
                {(properties.Count > 0 ? (cls == "ObjectModel" ? "else if (jsonProperty.Name != \"seqs\")" : "else") : "// no properties")}
                {{
                    Console.WriteLine(""[warn] Missing property {{0}} = {{1}} in {cls}"", jsonProperty.Name, jsonProperty.Value.GetRawText());
                }}
#endif 
            }}{(isDynamic ? "\n            return this;" : "")}
        }}", Encoding.UTF8);
        }
    }
}
