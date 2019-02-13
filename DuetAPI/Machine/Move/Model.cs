using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Move
{
    public class Model : ICloneable
    {
        public List<Axis> Axes { get; set; } = new List<Axis>();
        public double BabystepZ { get; set; }                                                   // mm
        public CurrentMove CurrentMove { get; set; } = new CurrentMove();
        public string Compensation { get; set; } = "None";
        public List<Drive> Drives { get; set; } = new List<Drive>();
        public List<Extruder> Extruders { get; set; } = new List<Extruder>();
        public Geometry Geometry { get; set; } = new Geometry();
        public Idle Idle { get; set; } = new Idle();
        public double SpeedFactor { get; set; } = 1.0;

        public object Clone()
        {
            return new Model
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
