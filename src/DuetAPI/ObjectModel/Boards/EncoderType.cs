using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Type of the encoder a closed loop driver reads its position from (M569.1 T)
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<EncoderType>))]
public enum EncoderType
{
    /// <summary>
    /// No encoder
    /// </summary>
    None = 0,

    /// <summary>
    /// Linear quadrature encoder plus a Duet3D magnetic shaft encoder
    /// </summary>
    LinearComposite = 1,

    /// <summary>
    /// Quadrature encoder on the motor shaft
    /// </summary>
    RotaryQuadrature = 2,

    /// <summary>
    /// Duet3D magnetic encoder on the motor shaft
    /// </summary>
    RotaryMagnetic = 3
}
