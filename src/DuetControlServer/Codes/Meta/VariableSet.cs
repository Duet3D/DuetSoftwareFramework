using System.Collections.Generic;
using System.Collections.Immutable;

namespace DuetControlServer.Codes.Meta;

/// <summary>
/// What happened to an attempt to assign to a variable
/// </summary>
/// <remarks>
/// Reported rather than thrown, because the wording of the refusal belongs to the statement that
/// asked for it - a G-code keyword answers in RepRapFirmware's words, and nothing else here should
/// have to know them
/// </remarks>
public enum VariableAssignment
{
    /// <summary>
    /// The value was stored
    /// </summary>
    Assigned,

    /// <summary>
    /// There is no variable of that name
    /// </summary>
    UnknownVariable,

    /// <summary>
    /// An index was applied to something that is not an array
    /// </summary>
    NotAnArray,

    /// <summary>
    /// An index was past the end of the array it was applied to
    /// </summary>
    IndexOutOfRange
}

/// <summary>
/// A local variable
/// </summary>
/// <param name="Value">Value it holds</param>
/// <param name="Depth">Depth of the code block that created it, and that deletes it when it ends</param>
internal readonly record struct LocalVariable(object? Value, int Depth);

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
    /// <remarks>Immutable, so that <see cref="Snapshot"/> copies nothing</remarks>
    private ImmutableDictionary<string, LocalVariable> _variables = ImmutableDictionary<string, LocalVariable>.Empty;

    /// <summary>
    /// Whether a snapshot may hold the arrays of the variables
    /// </summary>
    private bool _snapshotTaken;

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
            bool found = _variables.TryGetValue(name, out LocalVariable variable);
            value = variable.Value;
            return found;
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
    /// <param name="depth">Depth of the code block that creates it</param>
    /// <returns>True if it was created, false if a variable of that name already exists</returns>
    /// <remarks>
    /// RepRapFirmware refuses to let <c>var</c> reassign an existing variable, because a name that
    /// silently changes meaning halfway through a file is harder to find than an error
    /// </remarks>
    public bool TryCreateVariable(string name, object? value, int depth = 0)
    {
        lock (_lock)
        {
            if (_variables.ContainsKey(name))
            {
                return false;
            }
            _variables = _variables.Add(name, new LocalVariable(value, depth));
            return true;
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
            if (!_variables.TryGetValue(name, out LocalVariable existing))
            {
                return false;
            }
            _variables = _variables.SetItem(name, existing with { Value = value });
            return true;
        }
    }

    /// <summary>
    /// Assign to an element of an existing local variable
    /// </summary>
    /// <param name="name">Variable name without the <c>var.</c> prefix</param>
    /// <param name="indices">Index of the element, one per dimension</param>
    /// <param name="value">Value to give it</param>
    /// <returns>What happened</returns>
    /// <remarks>
    /// Done under the lock rather than by handing the array out, so that an assignment cannot race
    /// with a read of the same variable
    /// </remarks>
    public VariableAssignment TryAssignVariableElement(string name, IReadOnlyList<int> indices, object? value)
    {
        lock (_lock)
        {
            if (!_variables.TryGetValue(name, out LocalVariable existing))
            {
                return VariableAssignment.UnknownVariable;
            }
            if (!_snapshotTaken)
            {
                return AssignElement(existing.Value, indices, value);
            }

            VariableAssignment result = TryReplaceElement(existing.Value, indices, 0, value, out object? updated);
            if (result == VariableAssignment.Assigned)
            {
                _variables = _variables.SetItem(name, existing with { Value = updated });
            }
            return result;
        }
    }

    /// <summary>
    /// Copy the arrays an index chain goes through, with the element it names replaced
    /// </summary>
    /// <param name="target">Value the rest of the chain is applied to</param>
    /// <param name="indices">Index of the element, one per dimension</param>
    /// <param name="dimension">How many of the indices have been applied</param>
    /// <param name="value">Value to give the element</param>
    /// <param name="updated">Receives the copy</param>
    /// <returns>What happened</returns>
    private static VariableAssignment TryReplaceElement(object? target, IReadOnlyList<int> indices, int dimension, object? value, out object? updated)
    {
        updated = value;
        if (dimension == indices.Count)
        {
            return (dimension > 0) ? VariableAssignment.Assigned : VariableAssignment.NotAnArray;
        }
        if (target is not object?[] array)
        {
            return VariableAssignment.NotAnArray;
        }

        int index = indices[dimension];
        if (index < 0 || index >= array.Length)
        {
            return VariableAssignment.IndexOutOfRange;
        }

        VariableAssignment result = TryReplaceElement(array[index], indices, dimension + 1, value, out object? element);
        if (result == VariableAssignment.Assigned)
        {
            object?[] copy = [.. array];
            copy[index] = element;
            updated = copy;
        }
        return result;
    }

    /// <summary>
    /// The local variables as they are now
    /// </summary>
    /// <returns>Variables by name, for <see cref="Restore"/></returns>
    internal ImmutableDictionary<string, LocalVariable> Snapshot()
    {
        lock (_lock)
        {
            _snapshotTaken = true;
            return _variables;
        }
    }

    /// <summary>
    /// Put the local variables back to what they were
    /// </summary>
    /// <param name="snapshot">What <see cref="Snapshot"/> returned, of this set or of another</param>
    internal void Restore(ImmutableDictionary<string, LocalVariable> snapshot)
    {
        lock (_lock)
        {
            _snapshotTaken = true;
            _variables = snapshot;
        }
    }

    /// <summary>
    /// Walk an index chain and assign to the element it names
    /// </summary>
    /// <param name="target">Value the indices are applied to</param>
    /// <param name="indices">Index of the element, one per dimension</param>
    /// <param name="value">Value to give it</param>
    /// <returns>What happened</returns>
    /// <remarks>
    /// The array is mutated in place, so a caller holding the same array sees the change. That is
    /// what makes this work for a global as well, whose array is rewritten by its owner afterwards
    /// </remarks>
    internal static VariableAssignment AssignElement(object? target, IReadOnlyList<int> indices, object? value)
    {
        for (int i = 0; i < indices.Count; i++)
        {
            if (target is not object?[] array)
            {
                return VariableAssignment.NotAnArray;
            }
            if (indices[i] < 0 || indices[i] >= array.Length)
            {
                return VariableAssignment.IndexOutOfRange;
            }

            if (i == indices.Count - 1)
            {
                array[indices[i]] = value;
                return VariableAssignment.Assigned;
            }
            target = array[indices[i]];
        }
        return VariableAssignment.NotAnArray;
    }

    /// <summary>
    /// Delete the local variables that a code block and the blocks in it created
    /// </summary>
    /// <param name="depth">Depth of the block</param>
    public void DeleteVariables(int depth)
    {
        lock (_lock)
        {
            foreach (KeyValuePair<string, LocalVariable> variable in _variables)
            {
                if (variable.Value.Depth >= depth)
                {
                    _variables = _variables.Remove(variable.Key);
                }
            }
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
            _variables = _variables.Clear();
            _parameters.Clear();
        }
    }
}
