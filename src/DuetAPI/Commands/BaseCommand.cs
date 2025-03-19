using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPI.Commands;

/// <summary>
/// Base class of a command.
/// When an instance of this class is processed in the control server, the connection identifier of the channel it was received from is assigned.
/// </summary>
public abstract class BaseCommand
{
    /// <summary>
    /// Creates a new instance of the BaseCommand
    /// </summary>
    protected BaseCommand() => Command = GetType().UnderlyingSystemType.Name;

    /// <summary>
    /// Name of the command to execute
    /// </summary>
    [JsonPropertyOrder(-1)]
    public string Command { get; set; }

    /// <summary>
    /// Invokes the command implementation
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the command</returns>
    public virtual Task<object?> InvokeAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException($"{Command} not implemented");

    /// <summary>
    /// Update the command object from a JSON element
    /// </summary>
    /// <param name="jsonElement">JSON element</param>
    public abstract void UpdateFromJson(JsonElement jsonElement);

    /// <summary>
    /// Update the command object from a JSON reader
    /// </summary>
    /// <param name="reader">JSON reader</param>
    public abstract void UpdateFromJsonReader(ref Utf8JsonReader reader);
}
