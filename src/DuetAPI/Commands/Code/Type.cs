using System.Text.Json;
using System;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Type of a generic G/M/T-code
    /// </summary>
    [JsonConverter(typeof(CodeTypeConverter))]
    public enum CodeType
    {
        /// <summary>
        /// Undetermined
        /// </summary>
        None = '\0',

        /// <summary>
        /// Whole line comment
        /// </summary>
        Comment = 'Q',

        /// <summary>
        /// Meta G-code keyword (not sent as a code to RRF)
        /// </summary>
        /// <remarks>
        /// Codes of this type are not sent to RRF in binary representation
        /// </remarks>
        Keyword = 'K',

        /// <summary>
        /// G-code
        /// </summary>
        GCode = 'G',

        /// <summary>
        /// M-code
        /// </summary>
        MCode = 'M',

        /// <summary>
        /// T-code
        /// </summary>
        TCode = 'T'
    }

    /// <summary>
    /// Converter class for CodeType
    /// </summary>
    public class CodeTypeConverter : JsonConverter<CodeType>
    {
        /// <summary>
        /// Read a JSON value and convert it to a HttpResponseType
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns>Deserialized value</returns>
        public override CodeType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string");
            }

            return reader.GetString() switch
            {
                "" => CodeType.None,
                "Q" => CodeType.Comment,
                "K" => CodeType.Keyword,
                "G" => CodeType.GCode,
                "M" => CodeType.MCode,
                "T" => CodeType.TCode,
                _ => throw new JsonException("Unknown code type")
            };
        }

        /// <summary>
        /// Write a HttpResponseType value to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, CodeType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                CodeType.None => "",
                CodeType.Comment => "Q",
                CodeType.Keyword => "K",
                CodeType.GCode => "G",
                CodeType.MCode => "M",
                CodeType.TCode => "T",
                _ => throw new JsonException("Unknown code type")
            });
        }
    }
}
