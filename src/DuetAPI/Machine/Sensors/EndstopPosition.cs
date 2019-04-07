namespace DuetAPI.Machine
{
    /// <summary>
    /// Position of a configured endstop
    /// </summary>
    public enum EndstopPosition
    {
        /// <summary>
        /// Endstop is not configured
        /// </summary>
        None = 0,

        /// <summary>
        /// Endstop is configured to be hit at the low axis end
        /// </summary>
        LowEnd,

        /// <summary>
        /// Endstop is configured to be hit at the high axis end
        /// </summary>
        HighEnd
    }
}
