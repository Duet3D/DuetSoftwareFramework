using System.Text.Json.Serialization;
using DuetAPI.Utility;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Spindle type
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<SpindleType>))]
public enum SpindleType
{
    /// <summary>
    /// Enable and direction
    /// </summary>
    EnaDir,

    /// <summary>
    /// Forward and reverse
    /// </summary>
    FwdRev
}
