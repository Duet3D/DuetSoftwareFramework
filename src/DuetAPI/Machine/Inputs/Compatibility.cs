using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Compatibility level for emulation
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
    public enum Compatibility
    {
        /// <summary>
        /// No emulation (same as RepRapFirmware)
        /// </summary>
        Me,

        /// <summary>
        /// Emulating RepRapFirmware
        /// </summary>
        RepRapFirmware,

        /// <summary>
        /// Emulating Marlin
        /// </summary>
        Marlin,

        /// <summary>
        /// Emulating Teacup
        /// </summary>
        Teacup,

        /// <summary>
        /// Emulating Sprinter
        /// </summary>
        Sprinter,

        /// <summary>
        /// Emulating Repetier
        /// </summary>
        Repetier,

        /// <summary>
        /// Special emulation for NanoDLP
        /// </summary>
        NanoDLP
    }
}
