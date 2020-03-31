using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// State of a heater
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter<HeaterState>))]
    public enum HeaterState : int
    {
        /// <summary>
        /// Heater is turned off
        /// </summary>
        Off = 0,

        /// <summary>
        /// Heater is in standby mode
        /// </summary>
        Standby,

        /// <summary>
        /// Heater is active
        /// </summary>
        Active,

        /// <summary>
        /// Heater is being tuned
        /// </summary>
        Tuning,

        /// <summary>
        /// Heater is offline
        /// </summary>
        Offline
    }
}
