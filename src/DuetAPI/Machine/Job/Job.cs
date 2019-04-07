using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current file job (if any)
    /// </summary>
    public class Job : ICloneable
    {
        /// <summary>
        /// Information about the file being processed
        /// </summary>
        public ParsedFileInfo File { get; set; } = new ParsedFileInfo();
        
        /// <summary>
        /// Current position in the file being processed (in bytes or null)
        /// </summary>
        public long? FilePosition { get; set; }
        
        /// <summary>
        /// Name of the last file processed or null if none
        /// </summary>
        public string LastFileName { get; set; }
        
        /// <summary>
        /// Indicates if the last file processed was simulated
        /// </summary>
        public bool LastFileSimulated { get; set; }
        
        /// <summary>
        /// Virtual amounts of extruded filament according to the G-code file (in mm)
        /// </summary>
        public double[] ExtrudedRaw { get; set; } = new double[0];
        
        /// <summary>
        /// Total duration of the current job (in s)
        /// </summary>
        public double? Duration { get; set; }
        
        /// <summary>
        /// Number of the current layer or 0 if none has been started yet
        /// </summary>
        public int? Layer { get; set; }
        
        /// <summary>
        /// Time elapsed since the beginning of the current layer (in s)
        /// </summary>
        public double? LayerTime { get; set; }
        
        /// <summary>
        /// Information about the past layers
        /// </summary>
        /// <seealso cref="Layer"/>
        public List<Layer> Layers { get; set; } = new List<Layer>();
        
        /// <summary>
        /// Time needed to heat up the heaters (in s)
        /// </summary>
        public double? WarmUpDuration { get; set; }
        
        /// <summary>
        /// Estimated times left
        /// </summary>
        public TimesLeft TimesLeft { get; set; } = new TimesLeft();

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Job
            {
                File = (ParsedFileInfo)File.Clone(),
                FilePosition = FilePosition,
                LastFileName = (LastFileName != null) ? string.Copy(LastFileName) : null,
                LastFileSimulated = LastFileSimulated,
                ExtrudedRaw = (double[])ExtrudedRaw.Clone(),
                Duration = Duration,
                Layer = Layer,
                LayerTime = LayerTime,
                Layers = Layers.Select(layer => (Layer)layer.Clone()).ToList(),
                WarmUpDuration = WarmUpDuration,
                TimesLeft = (TimesLeft)TimesLeft.Clone()
            };
        }
    }
}
