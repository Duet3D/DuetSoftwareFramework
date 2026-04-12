using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DuetSharedLibrary;

/// <summary>
/// JSON converter for <see cref="Regex"/> that serializes the pattern (and options, when non-default)
/// in a human-readable form. Accepts either a bare pattern string or an object with
/// <c>Pattern</c> and optional <c>Options</c> fields on read.
/// </summary>
public sealed class RegexJsonConverter : JsonConverter<Regex>
{
    private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    /// <inheritdoc />
    public override Regex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new Regex(reader.GetString()!, DefaultOptions);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a string pattern or object with Pattern/Options");
        }

        string? pattern = null;
        RegexOptions regexOptions = DefaultOptions;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            string name = reader.GetString()!;
            reader.Read();
            if (string.Equals(name, "Pattern", StringComparison.OrdinalIgnoreCase))
            {
                pattern = reader.GetString();
            }
            else if (string.Equals(name, "Options", StringComparison.OrdinalIgnoreCase))
            {
                regexOptions = reader.TokenType == JsonTokenType.Number
                    ? (RegexOptions)reader.GetInt32()
                    : Enum.Parse<RegexOptions>(reader.GetString()!, ignoreCase: true);
            }
            else
            {
                reader.Skip();
            }
        }

        if (pattern == null)
        {
            throw new JsonException("Missing Pattern property");
        }
        return new Regex(pattern, regexOptions);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Regex value, JsonSerializerOptions options)
    {
        if (value.Options == DefaultOptions)
        {
            writer.WriteStringValue(value.ToString());
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("Pattern", value.ToString());
        writer.WriteNumber("Options", (int)value.Options);
        writer.WriteEndObject();
    }
}
