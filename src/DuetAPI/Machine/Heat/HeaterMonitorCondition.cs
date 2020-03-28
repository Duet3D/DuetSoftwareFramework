namespace DuetAPI.Machine
{
    /// <summary>
    /// Trigger condition for a heater monitor
    /// </summary>
    public enum HeaterMonitorCondition : int
    {
        /// <summary>
        /// Heater monitor is disabled
        /// </summary>
        Disabled = -1,

        /// <summary>
        /// Limit temperature has been exceeded
        /// </summary>
        TemperatureExceeded = 0,

        /// <summary>
        /// Limit temperature is too low
        /// </summary>
        TemperatureTooLow = 1
    }
}
