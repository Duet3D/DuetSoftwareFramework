namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Action to take when a heater monitor is triggered
    /// </summary>
    public enum HeaterMonitorAction : int
    {
        /// <summary>
        /// Generate a heater fault
        /// </summary>
        GenerateFault = 0,

        /// <summary>
        /// Permanently switch off the heater
        /// </summary>
        PermanentSwitchOff = 1,

        /// <summary>
        /// Temporarily switch off the heater until the condition is no longer met
        /// </summary>
        TemporarySwitchOff = 2,

        /// <summary>
        /// Shut down the printer
        /// </summary>
        ShutDown = 3
    }
}
