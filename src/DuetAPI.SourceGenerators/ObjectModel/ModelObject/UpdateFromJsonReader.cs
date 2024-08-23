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
    /// Generate ModelOBject.UpdateFromJsonReader method
    /// </summary>
    internal static class UpdateFromJsonReader
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

            string GeneratePropertyReadCalls()
            {
                using StringWriter stringWriter = new();
                using IndentedTextWriter writer = new(stringWriter)
                {
                    Indent = 5
                };

                bool first = true;
                foreach (var prop in properties)
                {
                    string jsonPropertyName = prop.GetJsonPropertyName(), propType = prop.GetPropertyType();

                    // (else) if (reader.ValueTextEquals(<propName>u8)) {
                    writer.WriteLine($"{(first ? "if" : "else if")} (reader.ValueTextEquals(\"{jsonPropertyName}\"u8))");
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine("reader.Read();");
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
                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "GrowingCollection" or "JsonModelDictionary" or "StaticModelDictionary" ||
                        receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                    {
                        if (prop.Type is NullableTypeSyntax nts)
                        {
                            writer.WriteLine("if (reader.TokenType == JsonTokenType.Null)");
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
                                writer.WriteLine($"{prop.Identifier.ValueText} = ({nts.ElementType}){prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                            }
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else if (receiver.DynamicModelObjectClasses.Contains(propType))
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText} = ({propType}){prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties)!;");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                        }
                    }
                    else if (propType is "ObservableCollection")
                    {
                        // Starting condition in case this value is nullable
                        if (prop.Type is NullableTypeSyntax)
                        {
                            writer.WriteLine("if (reader.TokenType == JsonTokenType.Null)");
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

                        bool isArray = false;
                        Tuple<string, string>? varNameAndItemGetter = genericPropType switch
                        {
                            "int" => new("newIntValue", "reader.GetInt32()"),
                            "string" => new("newStringValue", "reader.GetString()!"),
                            "char" => new("newCharValue", "reader.GetString()![0]"),
                            "float" => new("newFloatValue", "reader.GetSingle()"),
                            "float[]" => new("newFloatArrayValue", "ReadFloatArray(ref reader)"),
                            "int[]" => new("newIntArrayValue", "ReadIntArray(ref reader)"),
                            "DriverId" => new("newDriverIdValue", "new DriverId(reader.GetString()!)"),
                            _ => null
                        };
                        if (varNameAndItemGetter == null && receiver.Enums.Contains(genericPropType))
                        {
                            varNameAndItemGetter = new($"new{genericPropType}Value", $"JsonSerializer.Deserialize<{genericPropType}>(ref reader)");
                        }
                        else if (genericPropType is "float[]" or "int[]")
                        {
                            isArray = true;
                            writer.WriteLine($"{genericPropType} {varNameAndItemGetter!.Item2.Replace("ref", "ref Utf8JsonReader")}");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine("if (reader.TokenType == JsonTokenType.StartArray)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"List<{genericPropType.Trim('[', ']')}> values = new();");
                            writer.WriteLine("while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"values.Add(reader.{(genericPropType == "float[]" ? "GetSingle" : "GetInt32")}());");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("return values.ToArray();");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"throw new JsonException(\"Bad JSON token type {{reader.TokenType}} when trying to update {cls}\");");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        if (varNameAndItemGetter == null)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedType, prop.GetLocation(), jsonPropertyName, cls));
                            continue;
                        }

                        // Update or add items
                        writer.WriteLine("int i = 0;");
                        writer.WriteLine("while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"if (i >= {prop.Identifier.ValueText}.Count)");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{prop.Identifier.ValueText}.Add({(isNullableItemType ? $"(reader.TokenType == JsonTokenType.Null) ? null : " : "")}{varNameAndItemGetter.Item2});");
                        writer.Indent--;
                        writer.WriteLine("}");
                        if (isNullableItemType)
                        {
                            writer.WriteLine("else if (reader.TokenType == JsonTokenType.Null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"if ({prop.Identifier.ValueText}[i] != null)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText}[i] = null;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        writer.WriteLine("else");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{genericPropType} {varNameAndItemGetter.Item1} = {varNameAndItemGetter.Item2};");
                        if (isArray)
                        {
                            writer.WriteLine($"if (!{varNameAndItemGetter.Item1}.SequenceEqual({prop.Identifier.ValueText}[i]))");
                        }
                        else
                        {
                            writer.WriteLine($"if ({prop.Identifier.ValueText}[i] != {varNameAndItemGetter.Item1})");
                        }
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{prop.Identifier.ValueText}[i] = {varNameAndItemGetter.Item1};");
                        writer.Indent--;
                        writer.WriteLine("}");
                        writer.Indent--;
                        writer.WriteLine("}");
                        writer.WriteLine("i++;");
                        writer.Indent--;
                        writer.WriteLine("}");

                        // Delete obsolete items
                        writer.WriteLine($"while ({prop.Identifier.ValueText}.Count > i)");
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
                        // try
                        writer.WriteLine("try");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // assignment
                        if (propType is "DriverId")
                        {
                            if (prop.Type is NullableTypeSyntax)
                            {
                                writer.WriteLine("if (reader.TokenType == JsonTokenType.Null)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = new(reader.GetString()!);");
                                writer.Indent--;
                                writer.WriteLine("}");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = new(reader.GetString()!);");
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
                                    writer.WriteLine("if (reader.TokenType == JsonTokenType.Null)");
                                    writer.WriteLine("{");
                                    writer.Indent++;
                                    writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                                    writer.Indent--;
                                    writer.WriteLine("}");
                                    writer.WriteLine("else");
                                    writer.WriteLine("{");
                                    writer.Indent++;
                                    writer.WriteLine($"{prop.Identifier.ValueText} = reader.{getter};");
                                    writer.Indent--;
                                    writer.WriteLine("}");
                                }
                                else
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = reader.{getter};");
                                }
                            }
                            else if (receiver.Enums.Contains(propType))
                            {
                                if (prop.Type is NullableTypeSyntax)
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = (reader.TokenType == JsonTokenType.Null) ? null : JsonSerializer.Deserialize<{propType}>(ref reader);");
                                }
                                else
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText} = JsonSerializer.Deserialize<{propType}>(ref reader);");
                                }
                            }
                            else if (propType is "object")
                            {
                                writer.WriteLine("if (reader.TokenType == JsonTokenType.Null)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = null;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else if (reader.TokenType == JsonTokenType.String)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetString()!;");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else if (reader.TokenType == JsonTokenType.Number)");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetInt32();");
                                writer.Indent--;
                                writer.WriteLine("}");
                                writer.WriteLine("else");
                                writer.WriteLine("{");
                                writer.Indent++;
                                writer.WriteLine($"{prop.Identifier.ValueText} = System.Text.Encoding.UTF8.GetString(reader.ValueSpan.ToArray());");
                                writer.WriteLineNoTabs("#if VERIFY_OBJECT_MODEL");
                                writer.WriteLine($"Console.WriteLine($\"[warn] Unsupported token type {{reader.TokenType}} for property {jsonPropertyName} in {cls}\");");
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
                        writer.WriteLine($"catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof({prop.Type.ToString().TrimEnd('?')}), JsonElement.ParseValue(ref reader), e))");
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
                        writer.WriteLine("else");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine("reader.Skip();");
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
            bool useGeneratedUpdateFromJsonReader = methods.Any(mds => mds.Identifier.ValueText == "UpdateFromJsonReader" && mds.ParameterList.Parameters.Count == 2 && mds.ParameterList.Parameters[0].Identifier.ValueText == "reader" && mds.ParameterList.Parameters[1].Identifier.ValueText == "ignoreSbcProperties");

            // Generate method
            return SourceText.From($@"/// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""reader"">Reader to update this intance from</param>
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>{(isDynamic ? "\n        /// <returns>Updated instance</returns>" : "")}
        /// <exception cref=""JsonException"">Failed to deserialize data</exception>
        public {(isInherited ? "override " : isInheritedFrom ? "virtual " : "") + (isDynamic ? "IDynamicModelObject?" : "void")} {(useGeneratedUpdateFromJsonReader ? "Generated" : "")}UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {{
            if (reader.TokenType != JsonTokenType.StartObject)
            {{
                throw new JsonException(""expected start of object"");
            }}

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {{
                if (reader.TokenType == JsonTokenType.PropertyName)
                {{
                    {GeneratePropertyReadCalls()}
#if VERIFY_OBJECT_MODEL
                    {(properties.Count > 0 ? "else" : "// no properties")}
                    {{
                        Console.WriteLine(""[warn] Missing property {{0}} = {{1}} in {cls}"", jsonProperty.Name, jsonProperty.Value.GetRawText());
                    }}
#endif 
                }}
            }}{(isDynamic ? "\n            return this;" : "")}
        }}", Encoding.UTF8);
        }
    }
}
