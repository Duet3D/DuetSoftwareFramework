using System;

namespace DuetAPI.Machine
{
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
        public int Value { get; set; }
        
        /// <summary>
        /// Secondary value(s) of the probe
        /// </summary>
        public int[] SecondaryValues { get; set; }
        
        /// <summary>
        /// Configured trigger threshold (0..1023)
        /// </summary>
        public int Threshold { get; set; } = 500;
        
        /// <summary>
        /// Probe speed (in mm/s)
        /// </summary>
        public float Speed { get; set; } = 2F;
        
        /// <summary>
        /// Dive height (in mm)
        /// </summary>
        public float DiveHeight { get; set; }

        /// <summary>
        /// X+Y offsets (in mm)
        /// </summary>
        public float[] Offset { get; set; } = new float[2];

        /// <summary>
        /// Z height at which the probe is triggered (in mm)
        /// </summary>
        public float TriggerHeight { get; set; } = 0.7F;
        
        /// <summary>
        /// Whether the probe signal is inverted
        /// </summary>
        public bool Inverted { get; set; }
        
        /// <summary>
        /// Recovery time (in s)
        /// </summary>
        public float RecoveryTime { get; set; }
        
        /// <summary>
        /// Travel speed when probing multiple points (in mm/s)
        /// </summary>
        public float TravelSpeed { get; set; } = 100.0F;
        
        /// <summary>
        /// Maximum number of times to probe after a bad reading was determined
        /// </summary>
        public int MaxProbeCount { get; set; } = 1;
        
        /// <summary>
        /// Allowed tolerance deviation between two measures (in mm)
        /// </summary>
        public float Tolerance { get; set; } = 0.03F;
        
        /// <summary>
        /// Whether probing disables the bed heater(s)
        /// </summary>
        public bool DisablesBed { get; set; }

        /// <summary>
        /// Indicates if the probe parameters are supposed to be saved to config-override.g
        /// </summary>
        public bool Persistent { get; set; }

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
                SecondaryValues = (int[])SecondaryValues.Clone(),
                Threshold = Threshold,
                Speed = Speed,
                DiveHeight = DiveHeight,
                Offset = (float[])Offset.Clone(),
                TriggerHeight = TriggerHeight,
                Inverted = Inverted,
                RecoveryTime = RecoveryTime,
                TravelSpeed = TravelSpeed,
                MaxProbeCount = MaxProbeCount,
                Tolerance = Tolerance,
                DisablesBed = DisablesBed,
                Persistent = Persistent
            };
        }
    }
}