using DuetAPI;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes.Meta;

/// <summary>
/// Finds the variables a code can see, and owns the ones that belong to a channel rather than a file
/// </summary>
/// <remarks>
/// <para>
/// Which set a code sees is a single rule - the file it came from, or the channel it arrived on when
/// there is no file - and it is answered here so that every reader and writer of a variable agrees
/// about it.
/// </para>
/// <para>
/// Global variables are not held here. They live in <c>global</c> in the object model, because they
/// outlive every file, are visible to every channel and have to reach the clients over IPC like the
/// rest of the machine state
/// </para>
/// </remarks>
/// <param name="model">Object model holding the global variables</param>
public sealed class VariableStore(Model.ObjectModel model)
{
    /// <summary>
    /// Variables of codes that arrive on a channel without a file behind them
    /// </summary>
    /// <remarks>
    /// The equivalent of RepRapFirmware's top-level machine state, which is never popped, so these
    /// live as long as the process
    /// </remarks>
    private readonly VariableSet[] _channelVariables = [.. Enum.GetValues<CodeChannel>().Select(_ => new VariableSet())];

    /// <summary>
    /// Get the variables the given code can see
    /// </summary>
    /// <param name="code">Code being executed</param>
    /// <returns>Variables in scope</returns>
    public VariableSet For(Code code) => code.File?.Variables ?? _channelVariables[(int)code.Channel];

    /// <summary>
    /// Split a variable name from the element indices applied to it
    /// </summary>
    /// <param name="path">Name as written, e.g. <c>speeds[1][2]</c></param>
    /// <param name="name">Name on its own</param>
    /// <param name="indices">What stood in each pair of brackets, empty when the name stands alone</param>
    /// <returns>True if the name is a variable name, optionally indexed</returns>
    /// <remarks>
    /// One reader for this grammar, because two readers of one grammar diverge silently: the
    /// expression evaluator and the <c>set</c> statement have to agree on what <c>var.x[2]</c> names.
    /// What is inside the brackets is handed back as written, because the two do not agree on that:
    /// the expression parser has already evaluated its indices to integers by the time it asks, while
    /// <c>set</c> arrives with whatever the operator typed
    /// </remarks>
    public static bool TrySplitIndexedName(string path, out string name, out IReadOnlyList<string> indices)
    {
        name = path;
        indices = [];

        int bracket = path.IndexOf('[');
        if (bracket < 0)
        {
            return IsVariableName(path);
        }

        name = path[..bracket];
        if (!IsVariableName(name))
        {
            return false;
        }

        List<string> parsedIndices = [];
        int i = bracket, depth = 0, start = 0;
        for (; i < path.Length; i++)
        {
            if (path[i] == '[')
            {
                if (depth++ == 0)
                {
                    start = i + 1;
                }
            }
            else if (path[i] == ']')
            {
                if (--depth < 0)
                {
                    return false;
                }
                if (depth == 0)
                {
                    parsedIndices.Add(path[start..i]);
                }
            }
            else if (depth == 0)
            {
                return false;       // something between one pair of brackets and the next
            }
        }
        if (depth != 0)
        {
            return false;
        }

        indices = parsedIndices;
        return true;
    }

    /// <summary>
    /// Read an index that has already been evaluated to a number
    /// </summary>
    /// <param name="indices">Index expressions as written</param>
    /// <param name="values">The same indices as integers</param>
    /// <returns>True if every one of them is an integer literal</returns>
    /// <remarks>
    /// The expression parser evaluates an index before folding it into the path it asks about, so by
    /// the time an expression reaches the evaluator its indices are literals. Anything else is not
    /// something this can resolve
    /// </remarks>
    public static bool TryParseIndices(IReadOnlyList<string> indices, out IReadOnlyList<int> values)
    {
        int[] parsed = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            if (!int.TryParse(indices[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[i]))
            {
                values = [];
                return false;
            }
        }
        values = parsed;
        return true;
    }

    /// <summary>
    /// Check whether a string is a name a variable may have
    /// </summary>
    /// <param name="name">Name to check</param>
    /// <returns>True if it is a valid name</returns>
    private static bool IsVariableName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Create a global variable
    /// </summary>
    /// <param name="name">Variable name without the <c>global.</c> prefix</param>
    /// <param name="value">Value to give it</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if it was created, false if a global of that name already exists</returns>
    public async ValueTask<bool> TryCreateGlobalAsync(string name, object? value, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Global.ContainsKey(name))
            {
                return false;
            }
            model.Global[name] = ToJson(value);
            return true;
        }
    }

    /// <summary>
    /// Assign to an existing global variable
    /// </summary>
    /// <param name="name">Variable name without the <c>global.</c> prefix</param>
    /// <param name="value">Value to give it</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if it was assigned, false if there is no such global</returns>
    public async ValueTask<bool> TryAssignGlobalAsync(string name, object? value, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!model.Global.ContainsKey(name))
            {
                return false;
            }
            model.Global[name] = ToJson(value);
            return true;
        }
    }

    /// <summary>
    /// Assign to an element of an existing global variable
    /// </summary>
    /// <param name="name">Variable name without the <c>global.</c> prefix</param>
    /// <param name="indices">Index of the element, one per dimension</param>
    /// <param name="value">Value to give it</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What happened</returns>
    /// <remarks>
    /// A global is stored as JSON, so the element cannot be written in place: the array is read out,
    /// changed and written back, all under the same write lock
    /// </remarks>
    public async ValueTask<VariableAssignment> TryAssignGlobalElementAsync(string name, IReadOnlyList<int> indices, object? value,
                                                                          CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!model.Global.TryGetValue(name, out JsonElement? existing))
            {
                return VariableAssignment.UnknownVariable;
            }
            if (!TryFromJson(existing, out object? current))
            {
                return VariableAssignment.NotAnArray;
            }

            VariableAssignment result = VariableSet.AssignElement(current, indices, value);
            if (result == VariableAssignment.Assigned)
            {
                model.Global[name] = ToJson(current);
            }
            return result;
        }
    }

    /// <summary>
    /// Convert an evaluated value into what the object model stores
    /// </summary>
    /// <param name="value">Value produced by the expression evaluator</param>
    /// <returns>The same value as JSON</returns>
    /// <remarks>
    /// <para>
    /// The scalars written here are the scalars <see cref="TryFromJson"/> reads back, so what a
    /// variable may hold is stated once, by these two functions together.
    /// </para>
    /// <para>
    /// A null is stored as a JSON null rather than as an absent key, so that a global keeps existing
    /// after being set to null - <c>set global.x = null</c> assigns, it does not delete. Each value is
    /// written explicitly rather than serialized by reflection, so that the trimmed and AOT builds
    /// carry no dependency on runtime type inspection
    /// </para>
    /// </remarks>
    private static JsonElement ToJson(object? value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteValue(writer, value);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Write one value, which may be an array of them
    /// </summary>
    /// <param name="writer">Writer to write to</param>
    /// <param name="value">Value to write</param>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                break;
            case char charValue:
                writer.WriteStringValue(charValue.ToString());
                break;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                break;
            case int intValue:
                writer.WriteNumberValue(intValue);
                break;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                break;
            case long longValue:
                writer.WriteNumberValue(longValue);
                break;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                break;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                break;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                break;
            case DateTime dateTimeValue:
                writer.WriteStringValue(dateTimeValue);
                break;
            case object?[] array:
                writer.WriteStartArray();
                foreach (object? element in array)
                {
                    WriteValue(writer, element);
                }
                writer.WriteEndArray();
                break;
            case Parsing.ObjectModelValue objectValue:
                // An array of objects can be assigned to a variable, as it can in RepRapFirmware, and
                // this is what one of them prints as. Storing anything more would mean storing a
                // reference into the object model, which is what a global must not hold
                writer.WriteStringValue(objectValue.ToString());
                break;
            default:
                throw new ArgumentException($"Cannot store a value of type {value.GetType().Name} in a variable", nameof(value));
        }
    }

    /// <summary>
    /// Convert a stored global back into a value an expression can use
    /// </summary>
    /// <param name="element">Value as the object model stores it</param>
    /// <param name="value">The same value as a scalar</param>
    /// <returns>True if it is a scalar this can represent</returns>
    /// <remarks>
    /// An array is copied element by element, so what comes back is the caller's own and cannot be
    /// changed underneath it. Objects are refused: a variable has no way to hold one
    /// </remarks>
    public static bool TryFromJson(JsonElement? element, out object? value)
    {
        value = null;
        if (element is not JsonElement json)
        {
            return true;    // the key exists and holds nothing
        }

        switch (json.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = json.GetBoolean();
                return true;
            case JsonValueKind.String:
                value = json.GetString();
                return true;
            case JsonValueKind.Number:
                if (json.TryGetInt32(out int intValue))
                {
                    value = intValue;
                }
                else if (json.TryGetInt64(out long longValue))
                {
                    value = longValue;
                }
                else
                {
                    value = json.GetDouble();
                }
                return true;
            case JsonValueKind.Array:
                {
                    object?[] array = new object?[json.GetArrayLength()];
                    int index = 0;
                    foreach (JsonElement item in json.EnumerateArray())
                    {
                        if (!TryFromJson(item, out array[index++]))
                        {
                            return false;
                        }
                    }
                    value = array;
                    return true;
                }
            default:
                return false;
        }
    }
}
