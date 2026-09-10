using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Type of a configured endstop
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<EndstopType>))]
public enum EndstopType
{
    /// <summary>
    /// Generic input pin
    /// </summary>
    InputPin,

    /// <summary>
    /// Z-probe acts as an endstop
    /// </summary>
    ZProbeAsEndstop,

    /// <summary>
    /// Motor stall detection stops all the drives when triggered
    /// </summary>
    MotorStallAny,

    /// <summary>
    /// Motor stall detection stops individual drives when triggered
    /// </summary>
    MotorStallIndividual,

    /// <summary>
    /// Motor stall detected from the encoder position error rather than from StallGuard
    /// </summary>
    MotorStallEncoder,

    /// <summary>
    /// Unknown type
    /// </summary>
    Unknown
}
