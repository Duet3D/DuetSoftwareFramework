using System;

namespace DuetAPI.Machine.Move
{
    /// <summary>
    /// List of supported geometry types
    /// </summary>
    public static class GeometryType
    {
        public const string Cartesian = "cartesian";
        public const string CoreXY = "coreXY";
        public const string CoreXYU = "coreXYU";
        public const string CoreXYUV = "coreXYUV";
        public const string CoreXZ = "coreXZ";
        public const string Hangprinter = "Hangprinter";
        public const string Delta = "delta";
        public const string Polar = "Polar";
        public const string RotaryDelta = "Rotary delta";
        public const string Scara = "Scara";
    }

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