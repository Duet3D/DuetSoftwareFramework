namespace DuetAPI.Machine
{
    /// <summary>
    /// State of a heater (also see RepRapFirmware)
    /// </summary>
    public enum HeaterState
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
        Tuning
    }
}
