using System.Text.Json.Serialization;
using DuetAPI.Utility;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Enumeration of supported kinematics
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<KinematicsName>))]
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
    /// Linear delta
    /// </summary>
    LinearDelta,

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
