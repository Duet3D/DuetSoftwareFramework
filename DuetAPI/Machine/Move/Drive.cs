using System;

namespace DuetAPI.Machine.Move
{
    public class Drive : ICloneable
    {
        public double? Position { get; set; }                                                           // mm
        public DriveMicrostepping Microstepping { get; set; } = new DriveMicrostepping();
        public uint? Current { get; set; }                                                              // mA
        public double? Acceleration { get; set; }                                                       // mm/s^2
        public double? MinSpeed { get; set; }                                                           // mm/s
        public double? MaxSpeed { get; set; }                                                           // mm/s

        public object Clone()
        {
            return new Drive
            {
                Position = Position,
                Microstepping = (DriveMicrostepping)Microstepping.Clone(),
                Current = Current,
                Acceleration = Acceleration,
                MinSpeed = MinSpeed,
                MaxSpeed = MaxSpeed
            };
        }
    }
}