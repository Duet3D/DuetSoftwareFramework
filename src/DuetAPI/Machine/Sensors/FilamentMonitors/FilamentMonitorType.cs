using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Enumeration of supported filament sensors
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
    public enum FilamentMonitorType
    {
        /// <summary>
        /// Simple filament sensor
        /// </summary>
        Simple,

        /// <summary>
        /// Laser filament sensor
        /// </summary>
        Laser,

        /// <summary>
        /// Pulsed filament sensor
        /// </summary>
        Pulsed,

        /// <summary>
        /// Rotating magnet filament sensor
        /// </summary>
        RotatingMagnet,

        /// <summary>
        /// Unknown sensor type
        /// </summary>
        Unknown
    }
}
