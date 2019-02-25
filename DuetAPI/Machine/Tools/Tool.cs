using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Tools
{
    /// <summary>
    /// Information about a configured tool
    /// </summary>
    public class Tool : ICloneable
    {
        /// <summary>
        /// Number of the tool
        /// </summary>
        public uint Number { get; set; }
        
        /// <summary>
        /// Active temperatures of the associated heaters (in degC)
        /// </summary>
        public double[] Active { get; set; } = new double[0];
        
        /// <summary>
        /// Standby temperatures of the associated heaters (in degC)
        /// </summary>
        public double[] Standby { get; set; } = new double[0];
        
        /// <summary>
        /// Name of the tool or null if unset
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Name of the currently loaded filament
        /// </summary>
        public string Filament { get; set; }
        
        /// <summary>
        /// List of associated fans (indices)
        /// </summary>
        public uint[] Fans { get; set; } = new uint[0];
        
        /// <summary>
        /// List of associated heaters (indices)
        /// </summary>
        public uint[] Heaters { get; set; } = new uint[0];
        
        /// <summary>
        /// Mix ratios of the associated extruder drives
        /// </summary>
        public double[] Mix { get; set; } = new double[0];
        
        /// <summary>
        /// Associated spindle (index)
        /// </summary>
        public int Spindle { get; set; } = -1;
        
        /// <summary>
        /// Associated axes. At present only X and Y can be mapped per tool.
        /// 
        /// The order is the same as the visual axes, so by default the layout is
        /// [
        ///   [0],        // X
        ///   [1]         // Y
        /// ]
        /// </summary>
        public List<uint[]> Axes { get; set; } = new List<uint[]>();
        
        /// <summary>
        /// Axis offsets (in mm)
        /// This list is in the same order as <see cref="DuetAPI.Machine.Move.Model.Axes"/>
        /// </summary>
        /// <seealso cref="DuetAPI.Machine.Move.Axis"/>
        public double[] Offsets { get; set; } = new double[0];                  // same order as Move.Axes

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Tool
            {
                Number = Number,
                Active = (double[])Active.Clone(),
                Standby = (double[])Standby.Clone(),
                Name = (Name != null) ? string.Copy(Name) : null,
                Filament = (Filament != null) ? string.Copy(Filament) : null,
                Fans = (uint[])Fans.Clone(),
                Heaters = (uint[])Heaters.Clone(),
                Mix = (double[])Mix.Clone(),
                Spindle = Spindle,
                Axes = Axes.Select(subAxes => (uint[])subAxes.Clone()).ToList(),
                Offsets = (double[])Offsets.Clone()
            };
        }
    }
}