using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Enumeration of possible states of the drivers of an axis or extruder
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<DriverState>))]
public enum DriverState
{
    /// <summary>
    /// Drivers are disabled
    /// </summary>
    Disabled,

    /// <summary>
    /// Drivers are enabled at idle current
    /// </summary>
    Idle,

    /// <summary>
    /// Drivers are enabled
    /// </summary>
    Enabled
}
