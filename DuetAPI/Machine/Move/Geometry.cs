using System;

namespace DuetAPI.Machine.Move
{
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

    public class Geometry : ICloneable
    {
        public string Type { get; set; }                    // one of GeometryType or null

        // TODO expand this on demand

        public object Clone()
        {
            return new Geometry
            {
                Type = (Type != null) ? string.Copy(Type) : null
            };
        }
    }
}