using System.Collections.Generic;

namespace DuetControlServer.Codes.Meta;

/// <summary>
/// The variables one execution context can see
/// </summary>
/// <remarks>
/// <para>
/// A file - a job file or a macro - owns one of these, and so does every code channel, for the codes
/// that arrive on it without a file behind them. RepRapFirmware keeps the equivalent per machine
/// state, which is the same granularity: a macro cannot see the variables of whatever started it, and
/// a variable outlives the code that created it but not the file.
/// </para>
/// <para>
/// Locals and parameters live side by side because they are addressed differently - <c>var.name</c>
/// against <c>param.name</c> - so one name can be both, and a lookup has to say which it wants.
/// Parameters are written once, when the macro is started, and are read-only afterwards: RRF's
/// <c>set</c> accepts only the <c>var.</c> and <c>global.</c> prefixes.
/// </para>
/// </remarks>
public sealed class VariableSet
{
    /// <summary>
    /// Guards both dictionaries
    /// </summary>
    /// <remarks>
    /// Variables are written from the code pipeline and read while an expression is being evaluated,
    /// which happens under the object model read lock rather than the file lock, so neither lock
    /// covers this
    /// </remarks>
    private readonly object _lock = new();

    /// <summary>
    /// Local variables, addressed as <c>var.name</c>
    /// </summary>
    private readonly Dictionary<string, object?> _variables = [];

    /// <summary>
    /// Parameters the file was called with, addressed as <c>param.name</c>
    /// </summary>
    private readonly Dictionary<string, object?> _parameters = [];

    /// <summary>
    /// Look up a local variable
    /// </summary>
    /// <param name="name">Variable name without the <c>var.</c> prefix</param>
    /// <param name="value">Value it holds</param>
    /// <returns>True if it exists</returns>
    public bool TryGetVariable(string name, out object? value)
    {
        lock (_lock)
        {
            return _variables.TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// Look up a parameter
    /// </summary>
    /// <param name="name">Parameter name without the <c>param.</c> prefix</param>
    /// <param name="value">Value it holds</param>
    /// <returns>True if it exists</returns>
    public bool TryGetParameter(string name, out object? value)
    {
        lock (_lock)
        {
            return _parameters.TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// Create a local variable
    /// </summary>
    /// <param name="name">Variable name without the <c>var.</c> prefix</param>
    /// <param name="value">Value to give it</param>
    /// <returns>True if it was created, false if a variable of that name already exists</returns>
    /// <remarks>
    /// RepRapFirmware refuses to let <c>var</c> reassign an existing variable, because a name that
    /// silently changes meaning halfway through a file is harder to find than an error
    /// </remarks>
    public bool TryCreateVariable(string name, object? value)
    {
        lock (_lock)
        {
            return _variables.TryAdd(name, value);
        }
    }

    /// <summary>
    /// Assign to an existing local variable
    /// </summary>
    /// <param name="name">Variable name without the <c>var.</c> prefix</param>
    /// <param name="value">Value to give it</param>
    /// <returns>True if it was assigned, false if there is no such variable</returns>
    public bool TryAssignVariable(string name, object? value)
    {
        lock (_lock)
        {
            if (!_variables.ContainsKey(name))
            {
                return false;
            }
            _variables[name] = value;
            return true;
        }
    }

    /// <summary>
    /// Delete a local variable
    /// </summary>
    /// <param name="name">Variable name without the <c>var.</c> prefix</param>
    /// <remarks>
    /// Called when the code block that created the variable ends. Deleting one that is not there is
    /// not an error: the block records the names it created, and a file that was aborted mid-block
    /// may never have reached the statement that created one of them
    /// </remarks>
    public void DeleteVariable(string name)
    {
        lock (_lock)
        {
            _variables.Remove(name);
        }
    }

    /// <summary>
    /// Give the set the parameters its file was called with
    /// </summary>
    /// <param name="parameters">Parameters by name, without the <c>param.</c> prefix</param>
    /// <remarks>
    /// Done once, before the file runs its first code, which is what makes parameters read-only for
    /// everything that follows
    /// </remarks>
    public void SetParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        lock (_lock)
        {
            _parameters.Clear();
            foreach (var kv in parameters)
            {
                _parameters[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>
    /// Forget every variable and parameter
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _variables.Clear();
            _parameters.Clear();
        }
    }
}
