using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a layer from a file being printed.
    /// Do not change these properties after an instance was added to the object model
    /// </summary>
    public class Layer : ICloneable
    {
        /// <summary>
        /// Duration of the layer (in s or null if unknown)
        /// </summary>
        public float? Duration { get; set; }

        /// <summary>
        /// Height of the layer (in mm or null if unknown)
        /// </summary>
        public float? Height { get; set; }

        /// <summary>
        /// Actual amount of filament extruded during this layer (in mm)
        /// </summary>
        public float[] Filament { get; set; } = new float[0];

        /// <summary>
        /// Fraction of the file printed during this layer (0..1 or null if unknown)
        /// </summary>
        public float? FractionPrinted { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            return new Layer
            {
                Duration = Duration,
                Height = Height,
                Filament = (float[])Filament.Clone(),
                FractionPrinted = FractionPrinted
            };
        }
    }
}