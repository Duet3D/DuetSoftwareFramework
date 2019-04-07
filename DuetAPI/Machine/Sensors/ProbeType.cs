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
        /// A simple umodulated probe (like dc42's infrared probe)
        /// </summary>
        Unmodulated,

        /// <summary>
        /// A modulated probe (like the original one shipped with the RepRapPro Ormerod)
        /// </summary>
        Modulated,

        /// <summary>
        /// A probe that pulls the signal from HIGH to LOW when triggered
        /// </summary>
        ActiveLow,

        /// <summary>
        /// A probe that is connected to the E0 switch
        /// </summary>
        E0Switch,

        /// <summary>
        /// A probe that pulls the signal from LOW to HIGH when triggered
        /// </summary>
        ActiveHigh,

        /// <summary>
        /// A probe that is connected to the E1 switch
        /// </summary>
        E1Switch,

        /// <summary>
        /// A probe that is connected to the Z switch
        /// </summary>
        ZSwitch,

        /// <summary>
        /// An unfiltered probe that pulls the signal from low to high when triggered (unfiltered for faster reaction)
        /// </summary>
        UnfilteredActiveHigh,

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
