namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported probe types
    /// </summary>
    public enum ProbeType
    {
        /// <summary>
        /// No probe
        /// </summary>
        None = 0,

        /// <summary>
        /// A simple unmodulated probe (like dc42's infrared probe)
        /// </summary>
        Unmodulated,

        /// <summary>
        /// A modulated probe (like the original one shipped with the RepRapPro Ormerod)
        /// </summary>
        Modulated,

        /// <summary>
        /// A switch that is triggered when the probe is activated
        /// </summary>
        Switch,

        /// <summary>
        /// A BLTouch probe
        /// </summary>
        BLTouch,

        /// <summary>
        /// Motor load detection
        /// </summary>
        MotorLoadDetection
    }
}
