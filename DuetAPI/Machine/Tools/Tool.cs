using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Tools
{
    public class Tool : ICloneable
    {
        public uint Number { get; set; }
        public double[] Active { get; set; } = new double[0];
        public double[] Standby { get; set; } = new double[0];
        public string Name { get; set; }
        public string Filament { get; set; }
        public uint[] Fans { get; set; } = new uint[0];
        public uint[] Heaters { get; set; } = new uint[0];
        public double[] Mix { get; set; } = new double[0];
        public int Spindle { get; set; } = -1;
        public List<uint[]> Axes { get; set; } = new List<uint[]>();
        public double[] Offsets { get; set; } = new double[0];                  // same order as Move.Axes

        public object Clone()
        {
            return new Tool
            {
                Number = Number,
                Active = (double[])Active.Clone(),
                Standby = (double[])Standby.Clone(),
                Name = (Name != null) ? string.Copy(Name) : null,
                Filament = (Filament != null) ? string.Copy(Filament) : null,
                Fans = (uint[])Fans.Clone(),
                Heaters = (uint[])Heaters.Clone(),
                Mix = (double[])Mix.Clone(),
                Spindle = Spindle,
                Axes = Axes.Select(subAxes => (uint[])subAxes.Clone()).ToList(),
                Offsets = (double[])Offsets.Clone()
            };
        }
    }
}