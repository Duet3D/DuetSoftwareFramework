using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Nonlinear extrusion parameters (see M592)
    /// </summary>
    public class ExtruderNonlinear : ICloneable
    {
        /// <summary>
        /// A coefficient in the extrusion formula
        /// </summary>
        public float A { get; set; }
        
        /// <summary>
        /// B coefficient in the extrusion formula
        /// </summary>
        public float B { get; set; }
        
        /// <summary>
        /// Upper limit of the nonlinear extrusion compensation
        /// </summary>
        public float UpperLimit { get; set; } = 0.2F;
        
        /// <summary>
        /// Reserved for future use, for the temperature at which these values are valid (in degC)
        /// </summary>
        public float Temperature { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new ExtruderNonlinear
            {
                A = A,
                B = B,
                UpperLimit = UpperLimit,
                Temperature = Temperature
            };
        }
    }
}