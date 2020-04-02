using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Trigger condition for a heater monitor
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
    public enum HeaterMonitorCondition
    {
        /// <summary>
        /// Heater monitor is disabled
        /// </summary>
        Disabled,

        /// <summary>
        /// Limit temperature has been exceeded
        /// </summary>
        TooHigh,

        /// <summary>
        /// Limit temperature is too low
        /// </summary>
        TooLow,

        /// <summary>
        /// Unknown condition
        /// </summary>
        Undefined
    }
}
