using System;

namespace DuetAPI.Machine.Move
{
    /// <summary>
    /// Information about the configured geometry
    /// </summary>
    public class Geometry : ICloneable
    {
        /// <summary>
        /// Currently configured geometry type or null if unknown
        /// </summary>
        /// <seealso cref="GeometryType"/>
        public string Type { get; set; }

        // TODO expand this on demand

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Geometry
            {
                Type = (Type != null) ? string.Copy(Type) : null
            };
        }
    }
}