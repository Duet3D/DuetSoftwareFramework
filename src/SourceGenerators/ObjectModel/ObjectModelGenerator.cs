using DuetAPISrcGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SourceGenerators.ObjectModel
{
    /// <summary>
    /// Source code generator to annotate the main ObjectModel class for fast JSON deserialization and updates
    /// </summary>
    [Generator]
    public class ObjectModelGenerator : ISourceGenerator
    {
        /// <summary>
        /// Initialize the source generator
        /// </summary>
        /// <param name="context">Context</param>
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new ObjectModelSyntaxReceiver());
        }

        /// <summary>
        /// Execute the source generator
        /// </summary>
        /// <param name="context">Context</param>
        public void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is ObjectModelSyntaxReceiver receiver)
            {
                string GeneratePropertyUpdateCalls()
                {
                    StringWriter stringWriter = new();
                    IndentedTextWriter writer = new(stringWriter)
                    {
                        Indent = 3
                    };

                    foreach (var prop in receiver.PropertiesToAugment)
                    {
                        string jsonPropertyName = Helpers.GetJsonPropertyName(prop);
                        string propType = prop.Type is GenericNameSyntax gns ? gns.Identifier.ValueText : prop.Type is NullableTypeSyntax nts ? nts.ElementType.ToString() : prop.Type.ToString();

                        // if (key == <propName>) {
                        writer.WriteLine($"if (key == \"{jsonPropertyName}\")");
                        writer.WriteLine("{");
                        writer.Indent++;

                        // assignment
                        if (propType is "ModelCollection" or "ModelGrowingCollection" or "ModelDictionary" ||
                            receiver.Context.ModelCollectionClasses.Contains(propType) || receiver.Context.ModelObjectClasses.Contains(propType))
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
                                    writer.WriteLine($"{prop.Identifier.ValueText}.UpdateFromJson(jsonElement, ignoreSbcProperties);");
                                    writer.Indent--;
                                    writer.WriteLine("}");
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
                            context.ReportDiagnostic(Diagnostic.Create(Warnings.MissingMainOMKeyHandler, prop.GetLocation(), jsonPropertyName, propType));
                        }

                        // }
                        writer.Indent--;
                        writer.WriteLine("}");
                    }
                    return stringWriter.ToString();
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
        /// <returns>Whether the key could be updated</returns>q
        private bool InternalUpdateFromJson(string? key, JsonElement jsonElement, bool ignoreSbcProperties, int offset = 0, bool last = true)
        {{
            if (string.IsNullOrEmpty(key))
            {{
                UpdateFromJson(jsonElement, ignoreSbcProperties);
                return true;
            }}

            {GeneratePropertyUpdateCalls()}
            // Failed to find a property
#if VERIFY_OBJECT_MODEL
            Console.WriteLine(""[warn] Missing property: {{0}} = {{1}}"", key, jsonElement.GetRawText());
#endif
            return false;
        }}
    }}
}}", Encoding.UTF8);

                context.AddSource("ObjectModel.g.cs", sourceText);
            }
        }
    }

    class ObjectModelSyntaxReceiver : ISyntaxReceiver
    {
        public ObjectModelContext Context { get; } = new();

        public List<PropertyDeclarationSyntax> PropertiesToAugment { get; } = [];

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is ClassDeclarationSyntax cds)
            {
                if (cds.Identifier.ValueText == "ObjectModel")
                {
                    foreach (MemberDeclarationSyntax member in cds.Members)
                    {
                        if (member is PropertyDeclarationSyntax pds)
                        {
                            PropertiesToAugment.Add(pds);
                        }
                    }
                }
            }
            Context.CheckSyntaxNode(syntaxNode);
        }
    }
}
