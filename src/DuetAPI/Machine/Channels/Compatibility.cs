namespace DuetAPI.Machine
{
    /// <summary>
    /// Compatibility level for emulation
    /// </summary>
    public enum Compatibility
    {
        /// <summary>
        /// No emulation (same as RepRapFirmware)
        /// </summary>
        Me = 0,

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
