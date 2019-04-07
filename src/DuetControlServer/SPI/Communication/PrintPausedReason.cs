namespace DuetControlServer.SPI.Communication
{
    /// <summary>
    /// Reasons why a print has been paused
    /// </summary>
    public enum PrintPausedReason : byte
    {
        /// <summary>
        /// User-initiated pause (M26)
        /// </summary>
        User = 1,

        /// <summary>
        /// G-Code initiated pause (M226)
        /// </summary>
        GCode = 2,

        /// <summary>
        /// Filament change required (M600)
        /// </summary>
        FilamentChange = 3,

        /// <summary>
        /// Paused by trigger
        /// </summary>
        Trigger = 4,

        /// <summary>
        /// Paused due to heater fault
        /// </summary>
        HeaterFault = 5,

        /// <summary>
        /// Paused because of a filament sensor
        /// </summary>
        Filament = 6,

        /// <summary>
        /// Paused due to a motor stall
        /// </summary>
        Stall = 7,

        /// <summary>
        /// Paused due to a voltage drop
        /// </summary>
        LowVoltage = 8
    }
}
