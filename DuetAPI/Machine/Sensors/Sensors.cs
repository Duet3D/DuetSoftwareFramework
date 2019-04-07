using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about sensors
    /// </summary>
    public class Sensors : ICloneable
    {
        /// <summary>
        /// List of configured endstops
        /// </summary>
        /// <seealso cref="Endstop"/>
        public List<Endstop> Endstops { get; set; } = new List<Endstop>();
        
        /// <summary>
        /// List of configured probes
        /// </summary>
        /// <seealso cref="Probe"/>
        public List<Probe> Probes { get; set; } = new List<Probe>();

        // TODO add Sensors here holding info about thermistors

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Sensors
            {
                Endstops = Endstops.Select(endstop => (Endstop)endstop.Clone()).ToList(),
                Probes = Probes.Select(probe => (Probe)probe.Clone()).ToList()
            };
        }
    }
}