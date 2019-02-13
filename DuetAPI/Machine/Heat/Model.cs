using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Heat
{
    public class Model : ICloneable
    {
        public List<BedOrChamber> Beds { get; set; } = new List<BedOrChamber>();                // may contain null items
        public List<BedOrChamber> Chambers { get; set; } = new List<BedOrChamber>();            // may contain null items
        public double ColdExtrudeTemperature { get; set; } = 160;                               // degC
        public double ColdRetractTemperature { get; set; } = 90;                                // degC
        public List<ExtraHeater> Extra { get; set; } = new List<ExtraHeater>();
        public List<Heater> Heaters { get; set; } = new List<Heater>();

        public object Clone()
        {
            return new Model
            {
                Beds = Beds.Select(bed => (bed == null) ? null : (BedOrChamber)bed.Clone()).ToList(),
                Chambers = Chambers.Select(chamber => (chamber == null) ? null : (BedOrChamber)chamber.Clone()).ToList(),
                ColdExtrudeTemperature = ColdExtrudeTemperature,
                ColdRetractTemperature = ColdRetractTemperature,
                Extra = Extra.Select(extra => (ExtraHeater)extra.Clone()).ToList(),
                Heaters = Heaters.Select(heater => (Heater)heater.Clone()).ToList()
            };
        }
    }
}
