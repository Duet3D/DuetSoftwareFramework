using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    /*if (cls != "Board")
                    {
                        // debug
                        continue;
                    }*/

                    SourceText sourceText = SourceText.From($@"using System;
using System.Text.Json;

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
                    if (propType is "ModelCollection" or "ModelGrowingCollection" or "ModelDictionary" ||
                        receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                    {
                        void WriteSetOrUpdate()
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
                        }

                        if (Helpers.IsSbcProperty(prop))
                        {
                            writer.WriteLine("if (!ignoreSbcProperties)");
                            writer.WriteLine("{");
                            writer.Indent++;
                            WriteSetOrUpdate();
                            writer.Indent--;
                            writer.WriteLine("}");
                        }
                        else
                        {
                            WriteSetOrUpdate();
                        }
                    }
                    else if (propType is "string")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetString();");
                    }
                    else if (propType is "char")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetString()[0];");
                    }
                    else if (propType is "int")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetInt32();");
                    }
                    else if (propType is "bool")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetBoolean();");
                    }
                    else if (propType is "double")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetDouble();");
                    }
                    else if (propType is "float")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetSingle();");
                    }
                    else if (propType is "long")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetInt64();");
                    }
                    else if (propType is "ulong")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetUInt64();");
                    }
                    else if (propType is "uint")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetUInt32();");
                    }
                    else if (propType is "short")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetInt16();");
                    }
                    else if (propType is "ushort")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetUInt16();");
                    }
                    else if (propType is "byte")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetByte();");
                    }
                    else if (propType is "sbyte")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetSByte();");
                    }
                    else if (propType is "decimal")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetDecimal();");
                    }
                    else if (propType is "DateTime")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetDateTime();");
                    }
                    else if (propType is "DateTimeOffset")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetDateTimeOffset();");
                    }
                    else if (propType is "TimeSpan")
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = jsonProperty.Value.GetTimeSpan();");
                    }
                    else if (receiver.Enums.Contains(propType))
                    {
                        writer.WriteLine($"{prop.Identifier.ValueText} = JsonSerializer.Deserialize<{propType}>(jsonProperty.Value.GetRawText());");
                    }
                    else
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedType, prop.GetLocation(), jsonPropertyName, cls));
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
                    if (propType is "ModelCollection" or "ModelGrowingCollection" or "ModelDictionary" ||
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
                                    if (propType is "ModelCollection" or "ModelGrowingCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
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
                                    if (propType is "ModelCollection" or "ModelGrowingCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
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
                            else if (propType is "ModelCollection" or "ModelGrowingCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
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
                        writer.WriteLine($"{prop.Identifier.ValueText} = reader.GetString();");
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
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>{(isDynamic ? "\n/// <returns>Updated instance</returns>" : "")}
        /// <exception cref=""JsonException"">Failed to deserialize data</exception>
        public {(isDynamic ? "IDynamicModelObject?" : "void")} {(useGeneratedUpdateFromJson ? "Generated" : "")}UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
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
            }}{(isDynamic ? "\nreturn this;" : "")}
        }}
        
        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""reader"">JSON reader</param>
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>{(isDynamic ? "\n/// <returns>Updated instance</returns>" : "")}
        public {(isDynamic ? "IDynamicModelObject?" : "void")} {(useGeneratedUpdateFromJsonReader ? "Generated" : "")}UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
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
                            reader.Skip();  // Skip property name
#endif 
                            reader.Skip();  // Skip JSON value
                        }}
                        break;
                }}
            }}{(isDynamic ? "\nreturn this;" : "")}
        }}", Encoding.UTF8); 
        }
    }
}
