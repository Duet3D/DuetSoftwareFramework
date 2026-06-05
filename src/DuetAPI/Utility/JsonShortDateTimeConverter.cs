using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility;

/// <summary>
/// JSON converter for short DateTime values
/// </summary>
public class JsonShortDateTimeConverter : JsonConverter<DateTime>
{
    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return string.IsNullOrEmpty(value) ? throw new ArgumentNullException() : DateTime.Parse(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("s"));
    }
}
