using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuetAPI.SourceGenerators.Commands;

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
        List<PropertyDeclarationSyntax> properties = receiver.CommandMembers[cls];

        string GetJsonContext(string propType)
        {
            return receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType) || propType == "Message" ? "ObjectModel.ObjectModelContext" : "CommandContext";
        }
        string GeneratePropertyReadCalls()
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

                // (else) if (reader.ValueTextEquals(<propName>u8)) {
                writer.WriteLine($"{(first ? "if" : "else if")} (reader.ValueTextEquals(\"{jsonPropertyName}\"u8))");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("reader.Read();");
                first = false;

                // assignment
                if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" or "JsonModelDictionary" or "StaticModelDictionary" ||
                    receiver.CommandMembers.ContainsKey(propType) || receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
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
                            writer.WriteLine($"{prop.Identifier.ValueText} = ({nts.ElementType}){prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader);");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader);");
                        }
                        writer.Indent--;
                        writer.WriteLine("}");
                    }
                    else if (receiver.DynamicModelObjectClasses.Contains(propType))
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = ({propType}){prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader)!;");
                    }
                    else if (cls == "Move" && prop.Identifier.ValueText == "Axes")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, false, 0, last);");
                    }
                    else
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader);");
                    }
                }
                else if (propType is "List" or "ObservableCollection")
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
                    if (varNameAndItemGetter == null && receiver.Enums.Contains(genericPropType) || genericPropType == "CodeParameter")
                    {
                        varNameAndItemGetter = new($"new{genericPropType}Value", $"JsonSerializer.Deserialize(ref reader, {GetJsonContext(genericPropType)}.Default.{genericPropType})!");
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
                else if (prop.HasSetter())
                {
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
                        else if (receiver.Enums.Contains(propType) || propType == "Message")
                        {
                            if (prop.Type is NullableTypeSyntax)
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = (reader.TokenType == JsonTokenType.Null) ? null : JsonSerializer.Deserialize(ref reader, {GetJsonContext(propType)}.Default.{propType})!;");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = JsonSerializer.Deserialize(ref reader, {GetJsonContext(propType)}.Default.{propType})!;");
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
                            writer.WriteLine("if (reader.TryGetInt32(out int intValue))");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = intValue;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetSingle();");
                            writer.Indent--;
                            writer.WriteLine("}");
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
                        else if (propType == "JsonElement")
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText} = JsonElement.ParseValue(ref reader);");
                        }
                        else
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedType, prop.GetLocation(), jsonPropertyName, cls));
                        }
                    }
                }

                // }
                writer.Indent--;
                writer.WriteLine("}");
            }
            return stringWriter.ToString().TrimEnd();
        }

        // Generate method
        return SourceText.From($@"/// <summary>
    /// Update this instance from a given JSON element
    /// </summary>
    /// <remarks>This method is auto-generated</remarks>
    /// <param name=""reader"">Reader to update this intance from</param>
    /// <exception cref=""JsonException"">Failed to deserialize data</exception>
    public override void UpdateFromJsonReader(ref Utf8JsonReader reader)
    {{
        if (reader.TokenType == JsonTokenType.None && !reader.Read())
        {{
            throw new JsonException(""failed to read from JSON reader"");
        }}
        if (reader.TokenType != JsonTokenType.StartObject)
        {{
            throw new JsonException(""expected start of object"");
        }}

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {{
            if (reader.TokenType == JsonTokenType.PropertyName)
            {{
                {GeneratePropertyReadCalls()}
                {(properties.Count > 0 ? "else" : "// no properties")}
                {{
#if VERIFY_OBJECT_MODEL
                    string? propertyName = reader.GetString();
                    if (propertyName == ""command"")
                    {{
                        reader.Skip();
                    }}
                    else
                    {{
                        JsonElement jsonProperty = JsonDocument.ParseValue(ref reader).RootElement;
                        Console.WriteLine(""[warn] Missing property {{0}} = {{1}} in {cls}"", propertyName, jsonProperty.GetRawText());
                    }}
#else
                    reader.Skip();
#endif 
                }}
            }}
        }}
    }}{(cls == "Move" ? "\n        /// <summary>Wrapper function for JSON updates</summary>\n        /// <param name=\"reader\">JSON reader</param>\n        /// <param name=\"ignoreSbcProperties\">Ignore SBC properties</param>\n        public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties) => UpdateFromJsonReader(ref reader, ignoreSbcProperties, true);" : "")}", Encoding.UTF8);
    }
}
