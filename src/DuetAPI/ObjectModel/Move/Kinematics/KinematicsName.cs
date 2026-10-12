using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Enumeration of supported kinematics
/// </summary>
[JsonConverter(typeof(KinematicsNameConverter))]
public enum KinematicsName
{
    /// <summary>
    /// Cartesian
    /// </summary>
    Cartesian = 0,

    /// <summary>
    /// CoreXY
    /// </summary>
    CoreXY = 1,

    /// <summary>
    /// CoreXZ
    /// </summary>
    CoreXZ = 2,

    /// <summary>
    /// Linear delta
    /// </summary>
    LinearDelta = 3,

    /// <summary>
    /// SCARA
    /// </summary>
    Scara = 4,

    /// <summary>
    /// CoreXY with extra U axis
    /// </summary>
    CoreXYU = 5,

    /// <summary>
    /// Hangprinter
    /// </summary>
    Hangprinter = 6,

    /// <summary>
    /// Polar
    /// </summary>
    Polar = 7,

    /// <summary>
    /// CoreXY with extra UV axes
    /// </summary>
    CoreXYUV = 8,

    /// <summary>
    /// Five-bar SCARA
    /// </summary>
    FiveBarScara = 9,

    /// <summary>
    /// Rotary delta
    /// </summary>
    RotaryDelta = 10,

    /// <summary>
    /// MarkForged
    /// </summary>
    MarkForged = 11,

    /// <summary>
    /// Unknown
    /// </summary>
    Unknown
}

/// <summary>
/// Class to (de-)serialize kinematics names using the spellings reported by RepRapFirmware
/// (e.g. "delta" for linear delta and capitalized names like "Hangprinter" or "Rotary delta")
/// </summary>
public class KinematicsNameConverter : JsonConverter<KinematicsName>
{
    /// <inheritdoc />
    public override KinematicsName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Parse(reader.GetString());

    /// <summary>
    /// Resolve a kinematics name as RepRapFirmware spells it
    /// </summary>
    /// <param name="name">The name, case-insensitive</param>
    /// <returns>The kinematics, or <see cref="KinematicsName.Unknown"/></returns>
    /// <remarks>
    /// The same spellings M669 accepts and the object model reports, so a name that arrives as text -
    /// from JSON, from a code, from a report - resolves the same way wherever it came from
    /// </remarks>
    public static KinematicsName Parse(string? name)
    {
        return name?.ToLowerInvariant() switch
        {
            "cartesian" => KinematicsName.Cartesian,
            "corexy" => KinematicsName.CoreXY,
            "corexyu" => KinematicsName.CoreXYU,
            "corexyuv" => KinematicsName.CoreXYUV,
            "corexz" => KinematicsName.CoreXZ,
            "markforged" => KinematicsName.MarkForged,
            "fivebarscara" => KinematicsName.FiveBarScara,
            "hangprinter" => KinematicsName.Hangprinter,
            "delta" or "lineardelta" => KinematicsName.LinearDelta,
            "polar" => KinematicsName.Polar,
            "rotary delta" or "rotarydelta" => KinematicsName.RotaryDelta,
            "scara" => KinematicsName.Scara,
            _ => KinematicsName.Unknown,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, KinematicsName value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToName(value));

    /// <summary>
    /// Spell a kinematics name the way RepRapFirmware does
    /// </summary>
    /// <param name="value">The kinematics</param>
    /// <returns>Its name</returns>
    /// <remarks>
    /// This is what the object model reports and what M669 prints, and DuetWebControl, PanelDue and a
    /// decade of macros parse both. It is the one spelling of each geometry
    /// </remarks>
    public static string ToName(KinematicsName value)
    {
        return value switch
        {
            KinematicsName.Cartesian => "cartesian",
            KinematicsName.CoreXY => "coreXY",
            KinematicsName.CoreXYU => "coreXYU",
            KinematicsName.CoreXYUV => "coreXYUV",
            KinematicsName.CoreXZ => "coreXZ",
            KinematicsName.MarkForged => "markForged",
            KinematicsName.FiveBarScara => "FiveBarScara",
            KinematicsName.Hangprinter => "Hangprinter",
            KinematicsName.LinearDelta => "delta",
            KinematicsName.Polar => "Polar",
            KinematicsName.RotaryDelta => "Rotary delta",
            KinematicsName.Scara => "Scara",
            _ => "unknown",
        };
    }
}
