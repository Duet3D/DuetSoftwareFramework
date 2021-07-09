using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of possible input shaping methods
    /// </summary>
    [JsonConverter(typeof(MoveInputShapingTypeConverter))]
    public enum MoveInputShapingType
    {
        /// <summary>
        /// None
        /// </summary>
        None,

        /// <summary>
        /// ZVD
        /// </summary>
        ZVD,

        /// <summary>
        /// ZVDD
        /// </summary>
        ZVDD,

        /// <summary>
        /// EI2 (2-hump)
        /// </summary>
        EI2,

        /// <summary>
        /// EI3 (3-hump)
        /// </summary>
        EI3,

        /// <summary>
        /// DAA
        /// </summary>
        DAA
    }

    /// <summary>
    /// Class for easier access to JsonStringEnumConverter with camel-case naming
    /// </summary>
    public sealed class MoveInputShapingTypeConverter : JsonConverter<MoveInputShapingType>
    {
        /// <summary>
        /// Read an enum value from a JSON stream
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Deserialized value</returns>
        public override MoveInputShapingType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string stringValue = reader.GetString();
            return (MoveInputShapingType)Enum.Parse(typeof(MoveInputShapingType), stringValue, true);
        }

        /// <summary>
        /// Write an enum value to a JSON stream
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to write</param>
        /// <param name="options">Serializer options</param>
        public override void Write(Utf8JsonWriter writer, MoveInputShapingType value, JsonSerializerOptions options)
        {
            if (value == MoveInputShapingType.None)
            {
                writer.WriteStringValue("none");
            }
            else
            {
                writer.WriteStringValue(Enum.GetName(typeof(MoveInputShapingType), value));
            }
        }
    }
}
