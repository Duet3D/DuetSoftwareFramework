using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.IO;
using System.Text;

namespace DuetAPI.SourceGenerators.ObjectModel.ModelObject
{
    internal static class Generator
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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using DuetAPI.Utility;

#nullable enable
#pragma warning disable 618

namespace DuetAPI.ObjectModel
{{
    public partial class {cls}
    {{
        {GenerateMethods(context, receiver, cls)}
    }}
}}", Encoding.UTF8);

                    context.AddSource($"{cls}.g.cs", sourceText);
                }
            }
        }

        /// <summary>
        /// Generate ModelObject methods
        /// </summary>
        /// <param name="context">Generator context</param>
        /// <param name="receiver">Syntax receiver</param>
        /// <param name="cls">Class name</param>
        /// <returns>Generated methods</returns>
        public static string GenerateMethods(GeneratorExecutionContext context, SourceGeneratorSyntaxReceiver receiver, string cls)
        {
            using StringWriter stringWriter = new();
            using IndentedTextWriter writer = new(stringWriter)
            {
                Indent = 2
            };
            writer.WriteLine(UpdateFromJson.Generate(context, receiver, cls));
            writer.WriteLine();
            writer.WriteLine(UpdateFromJsonReader.Generate(context, receiver, cls));
            return stringWriter.ToString().TrimEnd();
        }
    }
}
