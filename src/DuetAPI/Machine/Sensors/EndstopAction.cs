namespace DuetAPI.Machine
{
    /// <summary>
    /// Action that is performed when an endstop is hit
    /// </summary>
    public enum EndstopAction
    {
        /// <summary>
        /// Don't stop anything
        /// </summary>
        None = 0,

        /// <summary>
        /// Reduce speed because an endstop or Z-probe is close to triggering
        /// </summary>
        ReduceSpeed,

        /// <summary>
        /// Stop a single motor driver
        /// </summary>
        StopDriver,

        /// <summary>
        /// Stop all drivers for an axis
        /// </summary>
        StopAxis,

        /// <summary>
        /// Stop all drives
        /// </summary>
        StopAll
    }
}
