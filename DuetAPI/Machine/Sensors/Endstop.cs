using System;

namespace DuetAPI.Machine.Sensors
{
    public enum EndstopPosition
    {
        None = 0,
        LowEnd,
        HighEnd
    }

    public enum EndstopType
    {
        ActiveLow = 0,
        ActiveHigh,
        Probe,
        MotorLoadDetection
    }

    public class Endstop : ICloneable
    {
        public bool Triggered { get; set; }
        public EndstopPosition Position { get; set; } = EndstopPosition.None;
        public EndstopType Type { get; set; }

        public object Clone()
        {
            return new Endstop
            {
                Triggered = Triggered,
                Position = Position,
                Type = Type
            };
        }
    }
}