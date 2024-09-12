using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuetAPI.SourceGenerators.ObjectModel
{
    internal static class Generator
    {
        /// <summary>
        /// Function to generate the additional ObjectModel source file
        /// </summary>
        /// <param name="context">Generator context</param>
        public static void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is SourceGeneratorSyntaxReceiver receiver && receiver.ModelObjectMembers.TryGetValue("ObjectModel", out List<PropertyDeclarationSyntax> properties))
            {
                string GeneratePropertyUpdateCalls()
                {
                    using StringWriter stringWriter = new();
                    using IndentedTextWriter writer = new(stringWriter)
                    {
                        Indent = 3
                    };

                    foreach (var prop in properties)
                    {
                        string jsonPropertyName = prop.GetJsonPropertyName(), propType = prop.GetPropertyType();

                        // if (key == <propName>) {
                        writer.WriteLine($"if (key == \"{jsonPropertyName}\")");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // assignment
                        if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" or "JsonModelDictionary" or "StaticModelDictionary" ||
                            receiver.ModelCollectionMembers.ContainsKey(propType) || receiver.ModelObjectMembers.ContainsKey(propType))
                        {
                            void WriteSetOrUpdate()
                            {
                                if (prop.Type is NullableTypeSyntax nts)
                                {
                                    writer.WriteLine("if (jsonElement.ValueKind == JsonValueKind.Null)");
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
                                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonElement, ignoreSbcProperties, offset, last);");
                                    }
                                    else
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonElement, ignoreSbcProperties);");
                                    }
                                    writer.Indent--;
                                    writer.WriteLine("}");
                                }
                                else if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonElement, ignoreSbcProperties, offset, last);");
                                }
                                else
                                {
                                    writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonElement, ignoreSbcProperties);");
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
                        else
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Descriptors.MissingMainOMKeyHandler, prop.GetLocation(), jsonPropertyName, propType));
                        }

                        // }
                        writer.Indent--;
                        writer.WriteLine("}");
                    }
                    return stringWriter.ToString().TrimEnd();
                }

                string WritePropertyReadCalls()
                {
                    using StringWriter stringWriter = new();
                    using IndentedTextWriter writer = new(stringWriter)
                    {
                        Indent = 3
                    };

                    foreach (var prop in properties)
                    {
                        string jsonPropertyName = prop.GetJsonPropertyName(), propType = prop.GetPropertyType();

                        // if (reader.ValueTextEquals(<propName>u8)) {
                        writer.WriteLine($"if (key == \"{jsonPropertyName}\")");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // read call
                        if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" or "JsonModelDictionary" or "StaticModelDictionary" ||
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
                                    if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties, offset, last);");
                                    }
                                    else
                                    {
                                        writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJsonReader(ref reader, ignoreSbcProperties);");
                                    }
                                    writer.Indent--;
                                    writer.WriteLine("}");
                                }
                                else if (propType is "DynamicModelCollection" or "StaticModelCollection" or "MessageCollection" || receiver.ModelCollectionMembers.ContainsKey(propType))
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
                                writer.WriteLine("reader.Skip();");
                                writer.WriteLine("return true;");
                            }
                            else
                            {
                                WriteSetOrUpdate();
                                writer.WriteLine("return true;");
                            }
                        }

                        // }
                        writer.Indent--;
                        writer.WriteLine("}");
                    }
                    return stringWriter.ToString().TrimEnd();
                }

                SourceText sourceText = SourceText.From($@"using System;
using System.Text.Json;

#nullable enable

namespace DuetAPI.ObjectModel
{{
    public partial class ObjectModel
    {{
        /// <summary>
        /// Update the whole or a specific key of this instance from a given JSON element
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""key"">Property name to update or null if the whole object model is supposed to be updated</param>
        /// <param name=""jsonElement"">Element to update this intance from</param>
        /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>
        /// <param name=""offset"">Index offset (collection keys only)</param>
        /// <param name=""last"">Whether this is the last update (collection keys only)</param>
        /// <returns>Whether the key could be updated</returns>
        private bool GeneratedUpdateFromJson(string? key, JsonElement jsonElement, bool ignoreSbcProperties, int offset = 0, bool last = true)
        {{
            if (string.IsNullOrEmpty(key))
            {{
                UpdateFromJson(jsonElement, ignoreSbcProperties);
                return true;
            }}

            {GeneratePropertyUpdateCalls()}
            if (key == ""move.axes"")
            {{
                Move.Axes.UpdateFromJson(jsonElement, ignoreSbcProperties, offset, last);
                return true;
            }}

#if VERIFY_OBJECT_MODEL
            if (key != ""seqs"")
            {{
                // Failed to find a property
                Console.WriteLine(""[warn] Missing property {{0}} = {{1}} in ObjectModel"", key, jsonElement.GetRawText());
            }}
#endif
            return false;
        }}

        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <remarks>This method is auto-generated</remarks>
        /// <param name=""key"">Property name to update or null if the whole object model is supposed to be updated</param>
        /// <param name=""reader"">JSON reader</param> /// <param name=""ignoreSbcProperties"">Whether SBC properties are ignored</param>
        /// <param name=""offset"">Index offset (collection keys only)</param>
        /// <param name=""last"">Whether this is the last update (collection keys only)</param>
        /// <returns>Whether the key could be updated</returns>
        private bool GeneratedUpdateFromJsonReader(string? key, ref Utf8JsonReader reader, bool ignoreSbcProperties, int offset = 0, bool last = true)
        {{
            if (string.IsNullOrEmpty(key))
            {{
                UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                return true;
            }}

            {WritePropertyReadCalls()}
            if (key == ""move.axes"")
            {{
                Move.Axes.UpdateFromJsonReader(ref reader, ignoreSbcProperties, offset, last);
                return true;
            }}
            return false;
        }}

        {ModelObject.Generator.GenerateMethods(context, receiver, "ObjectModel")}
    }}
}}", Encoding.UTF8);
                context.AddSource("ObjectModel.g.cs", sourceText);
            }
        }
    }
}
