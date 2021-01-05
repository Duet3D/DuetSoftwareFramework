using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// JSON converter for short DateTime values
    /// </summary>
    public class JsonShortDateTimeConverter : JsonConverter<DateTime?>
    {
        /// <summary>
        /// Read a short DateTime from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Deserialized DateTime or null</returns>
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string value = reader.GetString();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            return DateTime.Parse(value);
        }

        /// <summary>
        /// Write a short DateTime to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">Serializer options</param>
        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.Value.ToString("s"));
            }
        }
    }
}
