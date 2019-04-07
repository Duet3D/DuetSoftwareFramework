namespace DuetAPI.Machine
{
    /// <summary>
    /// Type of a configured endstop
    /// </summary>
    public enum EndstopType
    {
        /// <summary>
        /// The signal of the endstop is pulled from HIGH to LOW when hit
        /// </summary>
        ActiveLow = 0,

        /// <summary>
        /// The signal of the endstop is pulled from LOW to HIGH when hit
        /// </summary>
        ActiveHigh,

        /// <summary>
        /// A probe is used for this endstop
        /// </summary>
        Probe,

        /// <summary>
        /// Motor load detection is used for this endstop
        /// </summary>
        MotorLoadDetection
    }
}
