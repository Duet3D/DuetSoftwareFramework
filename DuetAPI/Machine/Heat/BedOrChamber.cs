using System;

namespace DuetAPI.Machine.Heat
{
    /// <summary>
    /// Information about a bed or chamber heater
    /// </summary>
    public class BedOrChamber : ICloneable
    {
        /// <summary>
        /// Active temperatures (in degC)
        /// </summary>
        public double[] Active { get; set; } = new double[0];
        
        /// <summary>
        /// Standby temperatures (in degC)
        /// </summary>
        public double[] Standby { get; set; } = new double[0];
        
        /// <summary>
        /// Name of the bed or chamber or null if unset
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Heaters controlled by this bed or chamber (indices)
        /// </summary>
        public int[] Heaters { get; set; } = new int[0];

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new BedOrChamber
            {
                Active = (double[])Active.Clone(),
                Standby = (double[])Standby.Clone(),
                Name = (Name != null) ? string.Copy(Name) : null,
                Heaters = (int[])Heaters.Clone()
            };
        }
    }
}