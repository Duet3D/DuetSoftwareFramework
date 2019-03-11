using System;

namespace DuetAPI.Machine.Job
{
    /// <summary>
    /// Information about a layer from a file being printed
    /// </summary>
    public class Layer : ICloneable
    {
        /// <summary>
        /// Duration of the layer (in s or null if unknown)
        /// </summary>
        public double? Duration { get; set; }

        /// <summary>
        /// Height of the layer (in mm or null if unknown)
        /// </summary>
        public double? Height { get; set; }

        /// <summary>
        /// Actual amount of filament extruded during this layer (in mm)
        /// </summary>
        public double[] Filament { get; set; } = new double[0];

        /// <summary>
        /// Fraction of the file printed during this layer (0..1 or null if unknown)
        /// </summary>
        public double? FractionPrinted { get; set; }

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
                Filament = (double[])Filament.Clone(),
                FractionPrinted = FractionPrinted
            };
        }
    }
}