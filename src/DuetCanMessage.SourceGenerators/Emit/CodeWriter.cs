using System.Text;
using System.Text.RegularExpressions;

namespace DuetCanMessage.SourceGenerators.Emit;

/// <summary>
/// A small indenting text writer used by both language emitters.
/// </summary>
public sealed class CodeWriter(string indentUnit = "\t")
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Indent() => _indent++;

    public void Outdent() => _indent--;

    public IDisposable Block(string header, string footer)
    {
        Line(header);
        Indent();
        return new Closer(this, footer);
    }

    public void Line(string text = "")
    {
        if (text.Length == 0)
        {
            _builder.Append('\n');
            return;
        }
        for (int i = 0; i < _indent; i++)
        {
            _builder.Append(indentUnit);
        }
        _builder.Append(text).Append('\n');
    }

    /// <summary>Write a documentation comment, splitting embedded newlines onto separate lines.</summary>
    public void Doc(string? doc, string prefix)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            return;
        }
        foreach (string line in doc.Split('\n'))
        {
            Line(prefix + line);
        }
    }

    /// <summary>Write an XML documentation comment for C#.</summary>
    /// <summary>
    /// Write a documentation comment for text taken from the schema, which is prose and may contain
    /// characters that XML would choke on.
    /// </summary>
    public void XmlDoc(string? doc) => WriteXmlDoc(doc, escape: true);

    /// <summary>
    /// Write a documentation comment the generator composed itself, so it may contain XML markup such as
    /// <c>&lt;see cref="..." /&gt;</c>. Anything originating in the schema has to be run through
    /// <see cref="Escape"/> before being included.
    /// </summary>
    public void XmlDocRaw(string? doc) => WriteXmlDoc(doc, escape: false);

    private void WriteXmlDoc(string? doc, bool escape)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            return;
        }
        string[] lines = [.. doc.Split('\n').Select(l => escape ? Escape(l) : l)];
        if (lines.Length == 1)
        {
            Line($"/// <summary>{lines[0]}</summary>");
            return;
        }
        Line("/// <summary>");
        foreach (string line in lines)
        {
            Line(line.Length == 0 ? "///" : $"/// {line}");
        }
        Line("/// </summary>");
    }

    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public override string ToString() => _builder.ToString();

    private sealed class Closer(CodeWriter writer, string footer) : IDisposable
    {
        public void Dispose()
        {
            writer.Outdent();
            writer.Line(footer);
        }
    }
}

/// <summary>
/// Naming conventions shared by the emitters.
/// </summary>
public static partial class Naming
{
    /// <summary>Convert a schema name (camelCase, possibly with underscores) to a C# PascalCase name.</summary>
    public static string Pascal(string name)
    {
        IEnumerable<string> parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        string result = string.Concat(parts);
        return result.Length == 0 ? name : result;
    }

    /// <summary>
    /// The C# spelling of an enumerator. PascalCasing covers almost all of them; the exception is the
    /// G-code subcommand message types, where CANlib writes <c>m569p1</c> and the C# enum has
    /// <c>M569P1</c>. The snake_case names CANlib uses for retired ids and for event types lose their
    /// underscores, exactly as a struct member's would.
    /// </summary>
    public static string MessageTypeMember(string cppName) =>
        SubcommandLetter().Replace(Pascal(cppName), m => m.Value.ToUpperInvariant());

    [GeneratedRegex(@"(?<=[0-9])p(?=[0-9])")]
    private static partial Regex SubcommandLetter();
}
