using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.CodeDom.Compiler;
using System.IO;
using System.Text;

namespace DuetAPI.SourceGenerators.Commands;

internal static class Generator
{
    /// <summary>
    /// Function to generate the additional ObjectModel source file
    /// </summary>
    /// <param name="context">Source production context</param>
    /// <param name="receiver">Syntax receiver</param>
    public static void Execute(SourceProductionContext context, SourceGeneratorSyntaxReceiver receiver)
    {
        foreach (string cls in receiver.CommandMembers.Keys)
        {
            SourceText sourceText = SourceText.From($@"using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using DuetAPI.Utility;

#nullable enable
#pragma warning disable 618

namespace DuetAPI.Commands;

public partial class {cls}
{{
    {GenerateMethods(context, receiver, cls)}
}}", Encoding.UTF8);

            context.AddSource($"{cls}.g.cs", sourceText);
        }
    }

    /// <summary>
    /// Generate ModelObject methods
    /// </summary>
    /// <param name="context">Source production context</param>
    /// <param name="receiver">Syntax receiver</param>
    /// <param name="cls">Class name</param>
    /// <returns>Generated methods</returns>
    public static string GenerateMethods(SourceProductionContext context, SourceGeneratorSyntaxReceiver receiver, string cls)
    {
        using StringWriter stringWriter = new();
        using IndentedTextWriter writer = new(stringWriter)
        {
            Indent = 1
        };
        writer.WriteLine(UpdateFromJson.Generate(context, receiver, cls));
        writer.WriteLine();
        writer.WriteLine(UpdateFromJsonReader.Generate(context, receiver, cls));
        return stringWriter.ToString().TrimEnd();
    }
}
