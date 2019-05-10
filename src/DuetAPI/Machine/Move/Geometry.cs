using System;

namespace DuetAPI.Machine
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

        /// <summary>
        /// Hangprinter A, B, C, Dz anchors
        /// </summary>
        public float[] Anchors { get; set; } = new float[10];

        /// <summary>
        /// Print radius for Hangprinter and Delta geometries in mm
        /// </summary>
        public float PrintRadius { get; set; }

        /// <summary>
        /// Delta diagonals
        /// </summary>
        public float[] Diagonals { get; set; } = new float[3];

        /// <summary>
        /// Delta radius in mm
        /// </summary>
        public float Radius { get; set; }

        /// <summary>
        /// Homed height of a delta printer in mm
        /// </summary>
        public float HomedHeight { get; set; }

        /// <summary>
        /// ABC angle corrections for delta geometries
        /// </summary>
        public float[] AngleCorrections { get; set; } = new float[3];

        /// <summary>
        /// Endstop adjustments of the XYZ axes in mm
        /// </summary>
        public float[] EndstopAdjustments { get; set; } = new float[3];

        /// <summary>
        /// Tilt values of the XY axes
        /// </summary>
        public float[] Tilt { get; set; } = new float[2];

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Geometry
            {
                Type = (Type != null) ? string.Copy(Type) : null,
                Anchors = (float[])Anchors.Clone(),
                PrintRadius = PrintRadius,
                Radius = Radius,
                HomedHeight = HomedHeight,
                AngleCorrections = (float[])AngleCorrections.Clone(),
                EndstopAdjustments = (float[])EndstopAdjustments.Clone(),
                Tilt = (float[])Tilt.Clone()
            };
        }
    }
}
