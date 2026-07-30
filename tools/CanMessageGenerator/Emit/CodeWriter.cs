using System.Text;

namespace CanMessageGenerator.Emit;

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
    public void XmlDoc(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            return;
        }
        string[] lines = doc.Split('\n');
        if (lines.Length == 1)
        {
            Line($"/// <summary>{Escape(lines[0])}</summary>");
            return;
        }
        Line("/// <summary>");
        foreach (string line in lines)
        {
            Line($"/// {Escape(line)}");
        }
        Line("/// </summary>");
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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
public static class Naming
{
    /// <summary>Convert a schema name (camelCase, possibly with underscores) to a C# PascalCase name.</summary>
    public static string Pascal(string name)
    {
        IEnumerable<string> parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        string result = string.Concat(parts);
        return result.Length == 0 ? name : result;
    }

    /// <summary>Convert a C++ CanMessageType enumerator (camelCase) to its C# PascalCase counterpart.</summary>
    public static string MessageTypeMember(string cppName) => char.ToUpperInvariant(cppName[0]) + cppName[1..];
}
