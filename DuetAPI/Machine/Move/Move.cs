using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the move subsystem
    /// </summary>
    public class Move : ICloneable
    {
        /// <summary>
        /// List of the configured axes
        /// </summary>
        /// <seealso cref="Axis"/>
        public List<Axis> Axes { get; set; } = new List<Axis>();
        
        /// <summary>
        /// Current babystep amount in Z direction (in mm)
        /// </summary>
        public double BabystepZ { get; set; }
        
        /// <summary>
        /// Information about the current move
        /// </summary>
        public CurrentMove CurrentMove { get; set; } = new CurrentMove();

        /// <summary>
        /// Name of the currently used bed compensation
        /// </summary>
        public string Compensation { get; set; } = "None";
        
        /// <summary>
        /// List of configured drives
        /// </summary>
        /// <seealso cref="Drive"/>
        public List<Drive> Drives { get; set; } = new List<Drive>();
        
        /// <summary>
        /// List of configured extruders
        /// </summary>
        /// <seealso cref="Extruder"/>
        public List<Extruder> Extruders { get; set; } = new List<Extruder>();
        
        /// <summary>
        /// Information about the currently configured geometry
        /// </summary>
        public Geometry Geometry { get; set; } = new Geometry();
        
        /// <summary>
        /// Idle current reduction parameters
        /// </summary>
        public Idle Idle { get; set; } = new Idle();
        
        /// <summary>
        /// Speed factor applied to every regular move (1.0 equals 100%)
        /// </summary>
        public double SpeedFactor { get; set; } = 1.0;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Move
            {
                Axes = Axes.Select(axis => (Axis)axis.Clone()).ToList(),
                BabystepZ = BabystepZ,
                CurrentMove = (CurrentMove)CurrentMove.Clone(),
                Compensation = (Compensation != null) ? string.Copy(Compensation) : null,
                Drives = Drives.Select(drive => (Drive)drive.Clone()).ToList(),
                Extruders = Extruders.Select(extruder => (Extruder)extruder.Clone()).ToList(),
                Geometry = (Geometry)Geometry.Clone(),
                Idle = (Idle)Idle.Clone(),
                SpeedFactor = SpeedFactor
            };
        }
    }
}
