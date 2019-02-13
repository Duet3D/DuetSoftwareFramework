using System;

namespace DuetAPI.Machine.Move
{
    public class CurrentMove : ICloneable
    {
        public double RequestedSpeed { get; set; }              // mm/s
        public double TopSpeed { get; set; }                    // mm/s

        public object Clone()
        {
            return new CurrentMove
            {
                RequestedSpeed = RequestedSpeed,
                TopSpeed = TopSpeed
            };
        }
    }
}