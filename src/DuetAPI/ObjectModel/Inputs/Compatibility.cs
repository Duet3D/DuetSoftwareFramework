using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Compatibility level for emulation
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Compatibility>))]
    public enum Compatibility
    {
        /// <summary>
        /// No emulation (same as RepRapFirmware)
        /// </summary>
        Default,

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
