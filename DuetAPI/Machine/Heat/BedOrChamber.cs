using System;

namespace DuetAPI.Machine.Heat
{
    public class BedOrChamber : ICloneable
    {
        public uint Number { get; set; }
        public double[] Active { get; set; } = new double[0];               // degC
        public double[] Standby { get; set; }                               // degC
        public string Name { get; set; }
        public uint[] Heaters { get; set; } = new uint[0];                  // indices

        public object Clone()
        {
            return new BedOrChamber
            {
                Number = Number,
                Active = (double[])Active.Clone(),
                Standby = (Standby != null) ? (double[])Standby.Clone() : null,
                Name = (Name != null) ? string.Copy(Name) : null,
                Heaters = (uint[])Heaters.Clone()
            };
        }
    }
}