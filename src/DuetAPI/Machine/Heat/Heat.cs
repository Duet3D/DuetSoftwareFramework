using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the heat subsystem
    /// </summary>
    public class Heat : ICloneable
    {
        /// <summary>
        /// List of configured beds
        /// </summary>
        /// <remarks>
        /// This may contain null items
        /// </remarks>
        /// <seealso cref="BedOrChamber"/>
        public List<BedOrChamber> Beds { get; set; } = new List<BedOrChamber>();
        
        /// <summary>
        /// List of configured chambers 
        /// </summary>
        /// <remarks>
        /// This may contain null items
        /// </remarks>
        /// <seealso cref="BedOrChamber"/>
        public List<BedOrChamber> Chambers { get; set; } = new List<BedOrChamber>();
        
        /// <summary>
        /// Minimum required temperature for extrusion moves (in degC)
        /// </summary>
        public double ColdExtrudeTemperature { get; set; } = 160;
        
        /// <summary>
        /// Minimum required temperature for retraction moves (in degC)
        /// </summary>
        public double ColdRetractTemperature { get; set; } = 90;
        
        /// <summary>
        /// List of configured extra heaters
        /// </summary>
        /// <seealso cref="ExtraHeater"/>
        public List<ExtraHeater> Extra { get; set; } = new List<ExtraHeater>();
        
        /// <summary>
        /// List of configured heaters
        /// </summary>
        /// <seealso cref="Heater"/>
        public List<Heater> Heaters { get; set; } = new List<Heater>();

        /// <summary>
        /// Creates a copy of this instance
        /// </summary>
        /// <returns>A copy of this instance</returns>
        public object Clone()
        {
            return new Heat
            {
                Beds = Beds.Select(bed => (BedOrChamber)bed?.Clone()).ToList(),
                Chambers = Chambers.Select(chamber => (BedOrChamber)chamber?.Clone()).ToList(),
                ColdExtrudeTemperature = ColdExtrudeTemperature,
                ColdRetractTemperature = ColdRetractTemperature,
                Extra = Extra.Select(extra => (ExtraHeater)extra.Clone()).ToList(),
                Heaters = Heaters.Select(heater => (Heater)heater.Clone()).ToList()
            };
        }
    }
}
