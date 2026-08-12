using DuetAPI;
using System;
using System.Buffers;
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
                default:
                    throw new ArgumentException($"Cannot store a value of type {value.GetType().Name} in a variable", nameof(value));
            }
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Convert a stored global back into a value an expression can use
    /// </summary>
    /// <param name="element">Value as the object model stores it</param>
    /// <param name="value">The same value as a scalar</param>
    /// <returns>True if it is a scalar this can represent</returns>
    /// <remarks>
    /// Arrays and objects are refused rather than converted. The expression evaluator hands back only
    /// immutable scalars, because everything else is read under the object model lock and used after
    /// it has been released
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
            default:
                return false;
        }
    }
}
