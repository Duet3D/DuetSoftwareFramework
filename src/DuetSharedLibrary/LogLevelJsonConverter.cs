using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DuetSharedLibrary;

/// <summary>
/// JSON converter that reads/writes <see cref="LogLevel"/> using the short lowercase aliases
/// defined in <see cref="LogLevelHelper"/> (e.g. "info", "warn", "debug").
/// </summary>
public sealed class LogLevelJsonConverter : JsonConverter<LogLevel>
{
    /// <inheritdoc />
    public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (LogLevel)reader.GetInt32();
        }
        return LogLevelHelper.ParseLogLevel(reader.GetString() ?? "Information");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options)
    {
        string name = value switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            LogLevel.None => "off",
            _ => value.ToString().ToLowerInvariant()
        };
        writer.WriteStringValue(name);
    }
}
