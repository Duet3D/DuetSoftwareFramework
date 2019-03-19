using System;

namespace DuetAPI.Machine.Channels
{
    /// <summary>
    /// Information about a G/M/T-code channel. May be further expanded
    /// </summary>
    public class Channel : ICloneable
    {
        /// <summary>
        /// Current feedrate in mm/s
        /// </summary>
        public float Feedrate { get; set; }

        /// <summary>
        /// Whether relative extrusion is being used
        /// </summary>
        public bool RelativeExtrusion { get; set; }

        /// <summary>
        /// Whether relative positioning is being used
        /// </summary>
        public bool RelativePositioning { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Channel
            {
                Feedrate = Feedrate,
                RelativeExtrusion = RelativeExtrusion,
                RelativePositioning = RelativePositioning
            };
        }
    }
}
