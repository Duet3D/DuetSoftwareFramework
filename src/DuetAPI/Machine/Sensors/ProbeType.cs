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
        Analog = 1,

        /// <summary>
        /// A modulated probe (like the original one shipped with the RepRapPro Ormerod)
        /// </summary>
        DumbModulated = 2,

        /// <summary>
        /// Alternate analog probe (like the ultrasonic probe)
        /// </summary>
        AlternateAnalog = 3,

        /// <summary>
        /// Endstop switch (obsolete, should not be used any more)
        /// </summary>
        /// <seealso cref="Digital"/>
        EndstopSwitch_Obsolete = 4,

        /// <summary>
        /// A switch that is triggered when the probe is activated (filtered)
        /// </summary>
        Digital = 5,

        /// <summary>
        /// Endstop switch on the E1 endstop pin (obsolete, should not be used any more)
        /// </summary>
        /// <seealso cref="Digital"/>
        E1Switch_Obsolete = 6,

        /// <summary>
        /// Endstop switch on Z endstop pin (obsolete, should not be used any more)
        /// </summary>
        /// <seealso cref="Digital"/>
        ZSwitch_Obsolete = 7,

        /// <summary>
        /// A switch that is triggered when the probe is activated (unfiltered)
        /// </summary>
        UnfilteredDigital = 8,

        /// <summary>
        /// A BLTouch probe
        /// </summary>
        BLTouch = 9,

        /// <summary>
        /// Z motor stall detection
        /// </summary>
        ZMotorStall = 10
    }
}
