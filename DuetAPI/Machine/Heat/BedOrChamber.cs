using System;

namespace DuetAPI.Machine.Heat
{
    /// <summary>
    /// Information about a bed or chamber heater
    /// </summary>
    public class BedOrChamber : ICloneable
    {
        /// <summary>
        /// Index of the bed or chamber heater
        /// </summary>
        public uint Number { get; set; }
        
        /// <summary>
        /// Active temperatures (in degC)
        /// </summary>
        public double[] Active { get; set; } = new double[0];
        
        /// <summary>
        /// Standby temperatures (in degC)
        /// </summary>
        public double[] Standby { get; set; }
        
        /// <summary>
        /// Name of the bed or chamber or null if unset
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Heaters controlled by this bed or chamber (indices)
        /// </summary>
        public uint[] Heaters { get; set; } = new uint[0];

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
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