using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Distance unit used for positioning
    /// </summary>
    [JsonConverter(typeof(DistanceUnitConverter))]
    public enum DistanceUnit
    {
        /// <summary>
        /// Millimeters
        /// </summary>
        MM,

        /// <summary>
        /// Inches
        /// </summary>
        Inch
    }

    /// <summary>
    /// Class used to convert distance units to and from JSON
    /// </summary>
    public class DistanceUnitConverter : JsonConverter<DistanceUnit>
    {
        /// <summary>
        /// Read a distance units from a JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">JSON options</param>
        /// <returns>Distance unit value</returns>
        public override DistanceUnit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException();
            }

            string jsonValue = reader.GetString()!;
            return jsonValue.Equals("in", StringComparison.InvariantCultureIgnoreCase) ? DistanceUnit.Inch : DistanceUnit.MM;
        }

        /// <summary>
        /// Write a distance units to a JSON writer
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Distance unit value</param>
        /// <param name="options">JSON options</param>
        public override void Write(Utf8JsonWriter writer, DistanceUnit value, JsonSerializerOptions options)
        {
            writer.WriteStringValue((value == DistanceUnit.MM) ? "mm" : "in");
        }
    }
}
