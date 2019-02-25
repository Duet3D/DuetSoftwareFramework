using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Sensors
{
    /// <summary>
    /// Information about sensors
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// List of configured endstops
        /// </summary>
        public List<Endstop> Endstops { get; set; } = new List<Endstop>();
        
        /// <summary>
        /// List of configured probes
        /// </summary>
        public List<Probe> Probes { get; set; } = new List<Probe>();

        // TODO add Sensors here holding info about thermistors

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
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