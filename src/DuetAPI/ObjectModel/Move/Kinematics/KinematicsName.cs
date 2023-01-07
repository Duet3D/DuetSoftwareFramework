using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of supported kinematics
    /// </summary>
    [JsonConverter(typeof(KinematicsNameConverter))]
    public enum KinematicsName
    {
        /// <summary>
        /// Cartesian
        /// </summary>
        Cartesian,

        /// <summary>
        /// CoreXY
        /// </summary>
        CoreXY,

        /// <summary>
        /// CoreXY with extra U axis
        /// </summary>
        CoreXYU,

        /// <summary>
        /// CoreXY with extra UV axes
        /// </summary>
        CoreXYUV,

        /// <summary>
        /// CoreXZ
        /// </summary>
        CoreXZ,

        /// <summary>
        /// MarkForged
        /// </summary>
        MarkForged,

        /// <summary>
        /// Five-bar SCARA
        /// </summary>
        FiveBarScara,

        /// <summary>
        /// Hangprinter
        /// </summary>
        Hangprinter,

        /// <summary>
        /// Linear Delta
        /// </summary>
        Delta,

        /// <summary>
        /// Polar
        /// </summary>
        Polar,

        /// <summary>
        /// Rotary delta
        /// </summary>
        RotaryDelta,

        /// <summary>
        /// SCARA
        /// </summary>
        Scara,

        /// <summary>
        /// Unknown
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Class to convert a <see cref="KinematicsName"/> to and from JSON
    /// </summary>
    public class KinematicsNameConverter : JsonConverter<KinematicsName>
    {
        /// <summary>
        /// Read a <see cref="KinematicsName"/> from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Read value</returns>
        public override KinematicsName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return (KinematicsName)reader.GetInt32();
            }
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString()!.ToLowerInvariant() switch
                {
                    "cartesian" => KinematicsName.Cartesian,
                    "corexy" => KinematicsName.CoreXY,
                    "corexyu" => KinematicsName.CoreXYU,
                    "corexyuv" => KinematicsName.CoreXYUV,
                    "corexz" => KinematicsName.CoreXZ,
                    "markforged" => KinematicsName.MarkForged,
                    "fivebarscara" => KinematicsName.FiveBarScara,
                    "hangprinter" => KinematicsName.Hangprinter,
                    "delta" => KinematicsName.Delta,
                    "polar" => KinematicsName.Polar,
                    "rotary delta" => KinematicsName.RotaryDelta,
                    "scara" => KinematicsName.Scara,
                    _ => KinematicsName.Unknown,
                };
            }
            throw new JsonException($"Invalid type for {nameof(KinematicsName)}");
        }

        /// <summary>
        /// Write a <see cref="KinematicsName"/> to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to serialize</param>
        /// <param name="options">Write options</param>
        public override void Write(Utf8JsonWriter writer, KinematicsName value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case KinematicsName.Cartesian:
                    writer.WriteStringValue("cartesian");
                    break;
                case KinematicsName.CoreXY:
                    writer.WriteStringValue("coreXY");
                    break;
                case KinematicsName.CoreXYU:
                    writer.WriteStringValue("coreXYU");
                    break;
                case KinematicsName.CoreXYUV:
                    writer.WriteStringValue("coreXYUV");
                    break;
                case KinematicsName.CoreXZ:
                    writer.WriteStringValue("coreXZ");
                    break;
                case KinematicsName.MarkForged:
                    writer.WriteStringValue("markForged");
                    break;
                case KinematicsName.FiveBarScara:
                    writer.WriteStringValue("FiveBarScara");
                    break;
                case KinematicsName.Hangprinter:
                    writer.WriteStringValue("Hangprinter");
                    break;
                case KinematicsName.Delta:
                    writer.WriteStringValue("delta");
                    break;
                case KinematicsName.Polar:
                    writer.WriteStringValue("Polar");
                    break;
                case KinematicsName.RotaryDelta:
                    writer.WriteStringValue("Rotary delta");
                    break;
                case KinematicsName.Scara:
                    writer.WriteStringValue("Scara");
                    break;
                default:
                    writer.WriteStringValue("unknown");
                    break;
            }
        }
    }
}
