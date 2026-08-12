namespace DuetControlServer.Codes.Meta.Parsing;

/// <summary>
/// Context supplying the environment-dependent parts of an expression evaluation: the values of context-sensitive
/// constants and the resolution of identifiers (object model fields, variables) and function calls.
/// The pure parser core (operators, literals, coercion) does not depend on this; only the parts that need to look
/// something up in the running system do
/// </summary>
/// <remarks>
/// The resolvers follow the Try-pattern instead of throwing: returning false means "this cannot be produced on the
/// SBC", which makes the parser abandon local evaluation so the whole expression is forwarded to the firmware. This
/// path is common (most expressions touch the object model), so it must not rely on exceptions. A genuine error in an
/// otherwise locally-handled construct (e.g. wrong argument type to a local function) should still throw a
/// <see cref="DuetAPI.CodeParserException"/>. Resolvers are only called while actually evaluating, not while merely
/// parsing a non-taken branch
/// </remarks>
public interface IExpressionEvaluationContext
{
    /// <summary>
    /// Current loop iteration count, or null when not inside a loop (the 'iterations' constant)
    /// </summary>
    int? Iterations { get; }

    /// <summary>
    /// Current G-code line number (the 'line' constant)
    /// </summary>
    int LineNumber { get; }

    /// <summary>
    /// How the last code on this channel ended (the 'result' constant), or null where there is no channel
    /// </summary>
    int? Result { get; }

    /// <summary>
    /// Try to resolve an identifier path such as <c>move.axes[0].machinePosition</c>, <c>var.foo</c> or
    /// <c>global.bar</c>. Array indices in the path have already been evaluated to integers
    /// </summary>
    /// <param name="path">Fully-qualified identifier path with resolved indices</param>
    /// <param name="wantExists">Caller only wants to know whether the path exists (from the exists() function)</param>
    /// <param name="wantArrayLength">The length operator '#' was applied, so return the array/string length</param>
    /// <param name="value">Resolved value (may be null when the path legitimately holds null)</param>
    /// <returns>True if resolved on the SBC, false to forward the expression to the firmware</returns>
    /// <exception cref="DuetAPI.CodeParserException">The path is handled locally but is in error</exception>
    bool TryResolveIdentifier(string path, bool wantExists, bool wantArrayLength, out object? value);

    /// <summary>
    /// Try to call a meta G-code function
    /// </summary>
    /// <param name="name">Function name</param>
    /// <param name="arguments">Evaluated arguments</param>
    /// <param name="wantArrayLength">The length operator '#' was applied to the call result</param>
    /// <param name="value">Function result</param>
    /// <returns>True if the function ran on the SBC, false to forward the expression to the firmware</returns>
    /// <exception cref="DuetAPI.CodeParserException">The function is handled locally but the call is in error</exception>
    bool TryCallFunction(string name, object?[] arguments, bool wantArrayLength, out object? value);
}
