using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Trigger condition for a heater monitor
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<HeaterMonitorCondition>))]
public enum HeaterMonitorCondition
{
    /// <summary>
    /// Heater monitor is disabled
    /// </summary>
    Disabled = -1,

    /// <summary>
    /// Limit temperature has been exceeded
    /// </summary>
    TooHigh = 0,

    /// <summary>
    /// Limit temperature is too low
    /// </summary>
    TooLow = 1
}
