using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about sensors
    /// </summary>
    public sealed class Sensors : IAssignable, ICloneable
    {
        /// <summary>
        /// List of configured endstops
        /// </summary>
        /// <seealso cref="Endstop"/>
        public ObservableCollection<Endstop> Endstops { get; set; } = new ObservableCollection<Endstop>();
        
        /// <summary>
        /// List of configured probes
        /// </summary>
        /// <seealso cref="Probe"/>
        public ObservableCollection<Probe> Probes { get; set; } = new ObservableCollection<Probe>();

        // TODO add Sensors here holding info about thermistors

        /// <summary>
        /// Assigns every property of another instance of this one
        /// </summary>
        /// <param name="from">Object to assign from</param>
        /// <exception cref="ArgumentNullException">other is null</exception>
        /// <exception cref="ArgumentException">Types do not match</exception>
        public void Assign(object from)
        {
            if (from == null)
            {
                throw new ArgumentNullException();
            }
            if (!(from is Sensors other))
            {
                throw new ArgumentException("Invalid type");
            }

            ListHelpers.AssignList(Endstops, other.Endstops);
            ListHelpers.AssignList(Probes, other.Probes);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Sensors clone = new Sensors();

            ListHelpers.CloneItems(clone.Endstops, Endstops);
            ListHelpers.CloneItems(clone.Probes, Probes);

            return clone;
        }
    }
}