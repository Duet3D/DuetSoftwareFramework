using System;

namespace DuetAPI.Machine.Sensors
{
    public enum ProbeType
    {
        None,
        Unmodulated,
        Modulated,
        ActiveLow,
        E0Switch,
        ActiveHigh,
        E1Switch,
        ZSwitch,
        UnfilteredActiveHigh,
        BLTouch,
        MotorLoadDetection
    }

    public class Probe : ICloneable
    {
        public ProbeType Type { get; set; }
        public uint Value { get; set; }
        public uint[] SecondaryValues { get; set; }
        public uint Threshold { get; set; } = 500;
        public double Speed { get; set; } = 2;                  // mm/s
        public double DiveHeight { get; set; }                  // mm
        public double TriggerHeight { get; set; } = 0.7;        // mm
        public bool Inverted { get; set; }
        public double RecoveryTime { get; set; } = 0.0;         // seconds
        public double TravelSpeed { get; set; } = 100.0;        // mm/s
        public uint MaxProbeCount { get; set; } = 1;
        public double Tolerance { get; set; } = 0.03;           // mm
        public bool DisablesBed { get; set; }

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