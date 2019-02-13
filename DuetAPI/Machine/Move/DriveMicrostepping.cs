using System;

namespace DuetAPI.Machine.Move
{
    public class DriveMicrostepping : ICloneable
    {
        public uint Value { get; set; } = 16;
        public bool Interpolated { get; set; } = true;

        public object Clone()
        {
            return new DriveMicrostepping
            {
                Value = Value,
                Interpolated = Interpolated
            };
        }
    }
}