using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported geometry types
    /// </summary>
    [JsonConverter(typeof(GeometryTypeConverter))]
    public enum GeometryType
    {
        /// <summary>
        /// Cartesian geometry
        /// </summary>
        Cartesian,

        /// <summary>
        /// CoreXY geometry
        /// </summary>
        CoreXY,

        /// <summary>
        /// CoreXY geometry with extra U axis
        /// </summary>
        CoreXYU,

        /// <summary>
        /// CoreXY geometry with extra UV axes
        /// </summary>
        CoreXYUV,

        /// <summary>
        /// CoreXZ geometry
        /// </summary>
        CoreXZ,

        /// <summary>
        /// Hangprinter geometry
        /// </summary>
        Hangprinter,

        /// <summary>
        /// Delta geometry
        /// </summary>
        Delta,

        /// <summary>
        /// Polar geometry
        /// </summary>
        Polar,

        /// <summary>
        /// Rotary delta geometry
        /// </summary>
        RotaryDelta,

        /// <summary>
        /// SCARA geometry
        /// </summary>
        Scara,

        /// <summary>
        /// Unknown geometry
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Class to convert a GeometryType to and from JSON
    /// </summary>
    /// <remarks>These enum values are primarily supplied by RepRapFirmware</remarks>
    public class GeometryTypeConverter : JsonConverter<GeometryType>
    {
        /// <summary>
        /// Read a GeometryType from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options"></param>
        /// <returns>Read value</returns>
        public override GeometryType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return (GeometryType)reader.GetInt32();
            }
            if (reader.TokenType == JsonTokenType.String)
            {
                return (reader.GetString().ToLowerInvariant()) switch
                {
                    "cartesian" => GeometryType.Cartesian,
                    "corexy" => GeometryType.CoreXY,
                    "corexyu" => GeometryType.CoreXYU,
                    "corexyuv" => GeometryType.CoreXYUV,
                    "corexz" => GeometryType.CoreXZ,
                    "hangprinter" => GeometryType.Hangprinter,
                    "delta" => GeometryType.Delta,
                    "polar" => GeometryType.Polar,
                    "rotary delta" => GeometryType.RotaryDelta,
                    "scara" => GeometryType.Scara,
                    _ => GeometryType.Unknown,
                };
            }
            throw new JsonException("Invalid GeometryType");
        }

        /// <summary>
        /// Write a GeometryType to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to serialize</param>
        /// <param name="options">Write options</param>
        public override void Write(Utf8JsonWriter writer, GeometryType value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case GeometryType.Cartesian:
                    writer.WriteStringValue("cartesian");
                    break;
                case GeometryType.CoreXY:
                    writer.WriteStringValue("coreXY");
                    break;
                case GeometryType.CoreXYU:
                    writer.WriteStringValue("coreXYU");
                    break;
                case GeometryType.CoreXYUV:
                    writer.WriteStringValue("coreXYUV");
                    break;
                case GeometryType.CoreXZ:
                    writer.WriteStringValue("coreXZ");
                    break;
                case GeometryType.Hangprinter:
                    writer.WriteStringValue("Hangprinter");
                    break;
                case GeometryType.Delta:
                    writer.WriteStringValue("delta");
                    break;
                case GeometryType.Polar:
                    writer.WriteStringValue("Polar");
                    break;
                case GeometryType.RotaryDelta:
                    writer.WriteStringValue("Rotary delta");
                    break;
                case GeometryType.Scara:
                    writer.WriteStringValue("Scara");
                    break;
                default:
                    writer.WriteStringValue("unknown");
                    break;
            }
        }
    }
}
