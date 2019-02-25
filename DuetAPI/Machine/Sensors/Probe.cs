using System;

namespace DuetAPI.Machine.Sensors
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

    /// <summary>
    /// Information about a configured probe
    /// </summary>
    public class Probe : ICloneable
    {
        /// <summary>
        /// Type of the configured probe
        /// </summary>
        /// <seealso cref="ProbeType"/>
        public ProbeType Type { get; set; }
        
        /// <summary>
        /// Current analog value of the probe
        /// </summary>
        public uint Value { get; set; }
        
        /// <summary>
        /// Secondary value(s) of the probe
        /// </summary>
        public uint[] SecondaryValues { get; set; }
        
        /// <summary>
        /// Configured trigger threshold (0..1023)
        /// </summary>
        public uint Threshold { get; set; } = 500;
        
        /// <summary>
        /// Probe speed (in mm/s)
        /// </summary>
        public double Speed { get; set; } = 2;
        
        /// <summary>
        /// Dive height (in mm)
        /// </summary>
        public double DiveHeight { get; set; }
        
        /// <summary>
        /// Z height at which the probe is triggered (in mm)
        /// </summary>
        public double TriggerHeight { get; set; } = 0.7;        // mm
        
        /// <summary>
        /// Whether the probe signal is inverted
        /// </summary>
        public bool Inverted { get; set; }
        
        /// <summary>
        /// Recovery time (in s)
        /// </summary>
        public double RecoveryTime { get; set; }
        
        /// <summary>
        /// Travel speed when probing multiple points (in mm/s)
        /// </summary>
        public double TravelSpeed { get; set; } = 100.0;
        
        /// <summary>
        /// Maximum number of times to probe after a bad reading was determined
        /// </summary>
        public uint MaxProbeCount { get; set; } = 1;
        
        /// <summary>
        /// Allowed tolerance deviation between two measures (in mm)
        /// </summary>
        public double Tolerance { get; set; } = 0.03;
        
        /// <summary>
        /// Whether probing disables the bed heater(s)
        /// </summary>
        public bool DisablesBed { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Probe
            {
                Type = Type,
                Value = Value,
                SecondaryValues = (uint[])SecondaryValues.Clone(),
                Threshold = Threshold,
                Speed = Speed,
                DiveHeight = DiveHeight,
                TriggerHeight = TriggerHeight,
                Inverted = Inverted,
                RecoveryTime = RecoveryTime,
                TravelSpeed = TravelSpeed,
                MaxProbeCount = MaxProbeCount,
                Tolerance = Tolerance,
                DisablesBed = DisablesBed
            };
        }
    }
}