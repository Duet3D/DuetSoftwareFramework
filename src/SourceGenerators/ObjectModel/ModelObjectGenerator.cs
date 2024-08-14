using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace SourceGenerators.ObjectModel
{
    internal static class ModelObjectGenerator
    {
        /// <summary>
        /// Function to generate the additional ObjectModel source file
        /// </summary>
        /// <param name="context">Generator context</param>
        public static void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is SourceGeneratorSyntaxReceiver receiver)
            {
                foreach (string cls in receiver.ModelObjectMembers.Keys)
                {
                    if (cls == "ObjectModel")
                    {
                        // This one has its own generator
                        continue;
                    }
                    if (receiver.IncompleteModelObjectClasses.TryGetValue(cls, out Location? location))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.IncompleteModelObjectClass, location, cls));
                        continue;
                    }

                    SourceText sourceText = SourceText.From($@"using System;
using System.Linq;
using System.Text.Json;
using DuetAPI.Utility;

#nullable enable

namespace DuetAPI.ObjectModel
{{
    public partial class {cls}
    {{
        {GenerateModelObjectMemembers(context, receiver, cls)}
    }}
}}", Encoding.UTF8);

                    context.AddSource($"{cls}.g.cs", sourceText);
                }
            }
        }

        /// <summary>
        /// Generate model object members
        /// </summary>
        /// <param name="context">Context</param>
        /// <param name="receiver">Syntax receiver</param>
        /// <param name="cls">Model object class</param>
        /// <returns>Generated code</returns>
        public static SourceText GenerateModelObjectMemembers(GeneratorExecutionContext context, SourceGeneratorSyntaxReceiver receiver, string cls)
        {
            List<PropertyDeclarationSyntax> properties = receiver.ModelObjectMembers[cls];
            List<MethodDeclarationSyntax> methods = receiver.ModelObjectMethods[cls];
            bool isDynamic = receiver.DynamicModelObjectClasses.Contains(cls);
            bool isInherited = receiver.InheritedClasses.Any(ic => ic.Key.Identifier.ValueText == cls), isInheritedFrom = receiver.InheritedClasses.Any(ic => ic.Value == cls);

            string GeneratePropertyUpdateCalls()
            {
                StringWriter stringWriter = new();
                IndentedTextWriter writer = new(stringWriter)
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

                    // assignment
                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "ModelGrowingCollection" or "ModelDictionary" ||
                        receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                    {
                        bool isSbcProperty = Helpers.IsSbcProperty(prop);
                        if (isSbcProperty)
                        {
                            writer.WriteLine("if (!ignoreSbcProperties)");
                            writer.WriteLine("{");
                            writer.Indent++;
                        }

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
                                writer.WriteLine($"{prop.Identifier.ValueText} = {prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);");
                            }
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);");
                        }

                        if (isSbcProperty)
                        {
                            writer.Indent--;
                            writer.WriteLine("}");
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
                            "char" => new("newCharValue", "GetString()[0]!"),
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
                            writer.WriteLine($"DriverId newDriverIdValue = new DriverId(jsonProperty.Value[i].GetString()!);");
                        }
                        else if (isEnum)
                        {
                            writer.WriteLine($"{genericPropType} new{genericPropType}Value = JsonSerializer.Deserialize<{genericPropType}>(jsonProperty.Value[i].GetRawText());");
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
                            writer.WriteLine($"{prop.Identifier.ValueText}.Add(JsonSerializer.Deserialize<{genericPropType}>(jsonProperty.Value[i].GetRawText()));");
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

                        // Closing brace in case this value is nullable
                        if (prop.Type is NullableTypeSyntax)
                        {
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                    }
                    else if (propType is "DriverId")
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
                            writer.WriteLine($"{prop.Identifier.ValueText} = new DriverId(jsonProperty.Value.GetString()!);");
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else
                        {
                            writer.WriteLine($"{prop.Identifier.ValueText} = new DriverId(jsonProperty.Value.GetString()!);");
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
                                writer.WriteLine($"{prop.Identifier.ValueText} = (jsonProperty.Value.ValueKind == JsonValueKind.Null) ? null : JsonSerializer.Deserialize<{propType}>(jsonProperty.Value.GetRawText());");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText} = JsonSerializer.Deserialize<{propType}>(jsonProperty.Value.GetRawText());");
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
                            writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetInt32();");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("else");
                            writer.WriteLine("{");
                            writer.Indent++;
                            writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetRawText();");
                            writer.WriteLine("#if VERIFY_OBJECT_MODEL");
                            writer.WriteLine($"Console.WriteLine($\"[warn] Unsupported object type {{jsonProperty.Value.ValueKind}} for property {jsonPropertyName} in {cls}\");");
                            writer.WriteLine("#endif");
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedType, prop.GetLocation(), jsonPropertyName, cls));
                        }
                    }

                    // }
                    writer.Indent--;
                    writer.WriteLine("}");
                }
                return stringWriter.ToString().TrimEnd();
            }

            string WritePropertyReadCalls()
            {
                StringWriter stringWriter = new();
                IndentedTextWriter writer = new(stringWriter)
                {
                    Indent = 6
                };

                bool first = true;
                foreach (var prop in properties)
                {
                    string jsonPropertyName = prop.GetJsonPropertyName(), propType = prop.GetPropertyType();

                    // (else) if (key == <propName>) {
                    writer.WriteLine($"{(first ? "if" : "else if")} (reader.ValueTextEquals(\"{jsonPropertyName}\"u8))");
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine("reader.Skip();");
                    first = false;

                    // read call
                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "ModelGrowingCollection" or "ModelDictionary" ||
                        receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                    {
                        void WriteSetOrUpdate()
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
                                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "ModelGrowingCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText} = {prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties, offset, last);");
                                    }
                                    else
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText} = {prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                                    }
                                }
                                else
                                {
                                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "ModelGrowingCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties, offset, last);");
                                    }
                                    else
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                                    }
                                }
                                writer.Indent--;
                                writer.WriteLine("}");
                            }
                            else if (propType is "DynamicModelCollection" or "StaticModelCollection" or "ModelGrowingCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties, offset, last);");
                            }
                            else
                            {
                                writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                            }
                        }

                        if (Helpers.IsSbcProperty(prop))
                        {
                            writer.WriteLine("if (!ignoreSbcProperties)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            WriteSetOrUpdate();
                            writer.WriteLine("return true;");
                            writer.Indent--;
                            writer.WriteLine("}");
                            writer.WriteLine("return false;");
                        }
                        else
                        {
                            WriteSetOrUpdate();
                            writer.WriteLine("return true;");
                        }
                    }
                    else if (propType is "string")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetString()!;");
                    }
                    else if (propType is "char")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetChar();");
                    }
                    else if (propType is "int")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetInt32();");
                    }
                    else if (propType is "bool")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetBoolean();");
                    }
                    else if (propType is "double")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetDouble();");
                    }
                    else if (propType is "float")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetSingle();");
                    }
                    else if (propType is "long")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetInt64();");
                    }
                    else if (propType is "ulong")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetUInt64();");
                    }
                    else if (propType is "uint")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetUInt32();");
                    }
                    else if (propType is "short")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetInt16();");
                    }
                    else if (propType is "ushort")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetUInt16();");
                    }
                    else if (propType is "byte")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetByte();");
                    }
                    else if (propType is "sbyte")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetSByte();");
                    }
                    else if (propType is "decimal")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetDecimal();");
                    }
                    else if (propType is "DateTime")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetDateTime();");
                    }
                    else if (propType is "DateTimeOffset")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetDateTimeOffset();");
                    }
                    else if (propType is "TimeSpan")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetTimeSpan();");
                    }
                    else if (receiver.Enums.Contains(propType))
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetEnum<{propType}>();");
                    }
                    else
                    {
                        writer.WriteLine("// unsupported type");
                    }

                    // }
                    writer.Indent--;
                    writer.WriteLine("}");
                }

                return stringWriter.ToString().TrimEnd();
            }

            // Check if we need to generate the UpdateFromJson(Reader) methods
            bool useGeneratedUpdateFromJson = methods.Any(mds => mds.Identifier.ValueText == "UpdateFromJson" && mds.ParameterList.Parameters.Count == 2 && mds.ParameterList.Parameters[0].Identifier.ValueText == "jsonElement" && mds.ParameterList.Parameters[1].Identifier.ValueText == "ignoreSbcProperties");
            bool useGeneratedUpdateFromJsonReader = methods.Any(mds => mds.Identifier.ValueText == "UpdateFromJsonReader" && mds.ParameterList.Parameters.Count == 2 && mds.ParameterList.Parameters[0].Identifier.ValueText == "reader" && mds.ParameterList.Parameters[1].Identifier.ValueText == "ignoreSbcProperties");

            // Generate methods
            return SourceText.From($@"/// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""jsonElement"">Element to update this intance from</param>
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>{(isDynamic ? "\n        /// <returns>Updated instance</returns>" : "")}
        /// <exception cref=""JsonException"">Failed to deserialize data</exception>
        public {((isInherited ? "override " : (isInheritedFrom ? "virtual " : "")) + (isDynamic ? "IDynamicModelObject?" : "void"))} {(useGeneratedUpdateFromJson ? "Generated" : "")}UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {{
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {{
                {(isDynamic ? "return null;" : "throw new ArgumentNullException(nameof(jsonElement));")}
            }}

            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {{
                {GeneratePropertyUpdateCalls()}
#if VERIFY_OBJECT_MODEL
                {(properties.Count > 0 ? "else" : "// no properties")}
                {{
                    Console.WriteLine(""[warn] Missing property {{0}} = {{1}} in {cls}"", jsonProperty.Name, jsonProperty.Value.GetRawText());
                }}
#endif 
            }}{(isDynamic ? "\n            return this;" : "")}
        }}", Encoding.UTF8);
#if false
        
        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""reader"">JSON reader</param>
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>{(isDynamic ? "\n        /// <returns>Updated instance</returns>" : "")}
        public {((isInherited ? "new " : "") + (isDynamic ? "IDynamicModelObject?" : "void"))} {(useGeneratedUpdateFromJsonReader ? "Generated" : "")}UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {{
            if (reader.TokenType == JsonTokenType.Null)
            {{
                {(isDynamic ? "return null;" : @"throw new JsonException(""property is not nullable"");")}
            }}
            if (reader.TokenType != JsonTokenType.StartObject)
            {{
                throw new JsonException(""expected start object token"");
            }}

            while (reader.Read())
            {{
                switch (reader.TokenType)
                {{
                    case JsonTokenType.PropertyName:
                        {WritePropertyReadCalls()}
                        {(properties.Count > 0 ? "else" : "// no properties")}
                        {{
#if VERIFY_OBJECT_MODEL
                            Console.WriteLine(""[warn] Missing property {{0}} in {cls}"", reader.GetString());
#else
                            reader.Skip();  // skip property name
#endif
                            reader.Skip();  // skip JSON value
                        }}
                        break;
                }}
            }}{(isDynamic ? "\n            return this;" : "")}
        }}", Encoding.UTF8); 
#endif
        }
    }
}
