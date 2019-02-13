using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Sensors
{
    public class Model : ICloneable
    {
        public List<Endstop> Endstops { get; set; } = new List<Endstop>();
        public List<Probe> Probes { get; set; } = new List<Probe>();

        // TODO add Sensors here holding info about thermistors

        public object Clone()
        {
            return new Model
            {
                Endstops = Endstops.Select(endstop => (Endstop)endstop.Clone()).ToList(),
                Probes = Probes.Select(probe => (Probe)probe.Clone()).ToList()
            };
        }
    }
}