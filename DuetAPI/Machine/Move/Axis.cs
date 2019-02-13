using System;

namespace DuetAPI.Machine.Move
{
    public class Axis : ICloneable
    {
        public char Letter { get; set; }                            // must be upper-case
        public uint[] Drives { get; set; } = new uint[0];
        public bool Homed { get; set; }
        public double? MachinePosition { get; set; }                // mm
        public double? Min { get; set; }                            // mm
        public double? Max { get; set; }                            // mm
        public bool Visible { get; set; } = true;

        public object Clone()
        {
            return new Axis
            {
                Letter = Letter,
                Drives = (uint[])Drives.Clone(),
                Homed = Homed,
                MachinePosition = MachinePosition,
                Min = Min,
                Max = Max,
                Visible = Visible
            };
        }
    }
}
