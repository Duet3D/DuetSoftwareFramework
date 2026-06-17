using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility;

/// <summary>
/// JSON converter for short DateTime values
/// </summary>
public class JsonOptionalShortDateTimeConverter : JsonConverter<DateTime?>
{
    /// <inheritdoc />
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return string.IsNullOrEmpty(value) ? null : DateTime.Parse(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value.ToString("s"));
        }
    }
}
