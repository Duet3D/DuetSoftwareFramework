namespace DuetControlServer.Codes.Meta.Parsing;

/// <summary>
/// Default evaluation context that resolves nothing locally: every identifier and function call reports that it must
/// be forwarded to the firmware, so any expression that depends on one is not evaluated on the SBC. Context-sensitive
/// constants take their neutral values. Used as the fallback when no richer context is supplied (e.g. in unit tests
/// of the pure parser core)
/// </summary>
public sealed class ForwardingExpressionContext : IExpressionEvaluationContext
{
    /// <summary>
    /// Shared instance
    /// </summary>
    public static ForwardingExpressionContext Instance { get; } = new();

    /// <inheritdoc/>
    public int? Iterations => null;

    /// <inheritdoc/>
    public int LineNumber => 0;

    /// <inheritdoc/>
    public int? Result => null;

    /// <inheritdoc/>
    public bool TryResolveIdentifier(string path, bool wantExists, bool wantArrayLength, out object? value)
    {
        value = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TryCallFunction(string name, object?[] arguments, bool wantArrayLength, out object? value)
    {
        value = null;
        return false;
    }
}
