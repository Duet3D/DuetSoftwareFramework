using System;
using System.Linq;
using DuetAPI.ObjectModel;
using NUnit.Framework.Constraints;

namespace SystemTests.Host;

/// <summary>
/// Constraints on the reply text <see cref="DcsTestHost.ExecuteCodeAsync"/> returns, which carry the
/// severity as the prefix <see cref="Message.ToString"/> gives it
/// </summary>
/// <remarks>
/// Every constraint trims the reply first, so a scenario never trims it to compare it. Each text a
/// constraint takes has to appear somewhere in the reply's content, after the code name and column
/// DuetControlServer puts in front of it; <see cref="ReplyConstraint.AtColumn"/> checks the column
/// in the parser's own scenarios. Shared refusals are worded in <see cref="CodeErrors"/>
/// </remarks>
internal static class Reply
{
    /// <summary>A reply with no text at all: the code succeeded and had nothing to report</summary>
    public static ReplyConstraint IsEmpty => new([MessageType.Success], [], empty: true);

    /// <summary>A reply that is neither an error nor a warning, containing every text given</summary>
    public static ReplyConstraint IsSuccess(params string?[] containing) => new([MessageType.Success], containing);

    /// <summary>A reply that is not an error: a success or a warning</summary>
    public static ReplyConstraint IsNotError => new([MessageType.Success, MessageType.Warning], []);

    /// <summary>A warning, containing every text given</summary>
    public static ReplyConstraint IsWarning(params string?[] containing) => new([MessageType.Warning], containing);

    /// <summary>An error, containing every text given</summary>
    public static ReplyConstraint IsError(params string?[] containing) => new([MessageType.Error], containing);

    /// <summary>A reply of the given severity, containing every text given</summary>
    public static ReplyConstraint Is(MessageType type, params string?[] containing) => new([type], containing);

    /// <summary>Split a reply into its severity and its content, as <see cref="Message.ToString"/> joined them</summary>
    public static (MessageType Type, string Content) Parse(string reply)
    {
        string text = reply.Trim();
        return text.StartsWith(ErrorPrefix, StringComparison.Ordinal) ? (MessageType.Error, text[ErrorPrefix.Length..])
             : text.StartsWith(WarningPrefix, StringComparison.Ordinal) ? (MessageType.Warning, text[WarningPrefix.Length..])
             : (MessageType.Success, text);
    }

    /// <summary>The non-empty lines of a reply, each trimmed</summary>
    public static string[] Lines(string reply) => reply.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private const string ErrorPrefix = "Error: ", WarningPrefix = "Warning: ";
}

/// <summary>A reply of one of the given severities, containing every text given</summary>
internal sealed class ReplyConstraint(MessageType[] types, string?[]? containing, bool empty = false) : Constraint
{
    private readonly string[] _containing = containing?.OfType<string>().ToArray() ?? [];

    private int? _column;

    /// <summary>Also require the content to start with the column DuetControlServer quotes for a refused parameter</summary>
    public ReplyConstraint AtColumn(int column)
    {
        _column = column;
        return this;
    }

    public override string Description
    {
        get
        {
            string severity = empty ? "an empty reply"
                            : types.Length > 1 ? "a reply that is not an error"
                            : types[0] switch
                            {
                                MessageType.Error => "an error",
                                MessageType.Warning => "a warning",
                                _ => "a reply that is neither an error nor a warning"
                            };
            return severity
                 + (_column is int column ? $" at column {column}" : string.Empty)
                 + (_containing.Length > 0 ? " containing " + string.Join(" and ", _containing.Select(text => $"\"{text}\"")) : string.Empty);
        }
    }

    public override ConstraintResult ApplyTo<TActual>(TActual actual)
    {
        if (actual is not string reply)
        {
            throw new ArgumentException($"A reply constraint applies to the reply text, not to {typeof(TActual).Name}", nameof(actual));
        }

        (MessageType type, string content) = Reply.Parse(reply);
        bool matches = types.Contains(type)
                    && (!empty || content.Length == 0)
                    && (_column is not int column || content.StartsWith($"at column {column}: ", StringComparison.Ordinal))
                    && _containing.All(text => content.Contains(text, StringComparison.Ordinal));
        return new ConstraintResult(this, actual, matches);
    }
}
