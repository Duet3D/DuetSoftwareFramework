using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Estimations about the times left
    /// </summary>
    public class TimesLeft : ICloneable
    {
        /// <summary>
        /// Time left based on file progress (in s or null)
        /// </summary>
        public float? File { get; set; }
        
        /// <summary>
        /// Time left based on filament consumption (in s or null)
        /// </summary>
        public float? Filament { get; set; }
        
        /// <summary>
        /// Time left based on the layer progress (in s or null)
        /// </summary>
        public float? Layer { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new TimesLeft
            {
                File = File,
                Filament = Filament,
                Layer = Layer
            };
        }
    }
}