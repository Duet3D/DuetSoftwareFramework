namespace DuetAPI.Machine.Move
{
    /// <summary>
    /// List of supported geometry types
    /// </summary>
    public static class GeometryType
    {
        /// <summary>
        /// Cartesian geometry
        /// </summary>
        public const string Cartesian = "cartesian";

        /// <summary>
        /// CoreXY geometry
        /// </summary>
        public const string CoreXY = "coreXY";

        /// <summary>
        /// CoreXY geometry with extra U axis
        /// </summary>
        public const string CoreXYU = "coreXYU";

        /// <summary>
        /// CoreXY geometry with extra UV axes
        /// </summary>
        public const string CoreXYUV = "coreXYUV";

        /// <summary>
        /// CoreXZ geometry
        /// </summary>
        public const string CoreXZ = "coreXZ";

        /// <summary>
        /// Hangprinter geometry
        /// </summary>
        public const string Hangprinter = "Hangprinter";

        /// <summary>
        /// Delta geometry
        /// </summary>
        public const string Delta = "delta";

        /// <summary>
        /// Polar geometry
        /// </summary>
        public const string Polar = "Polar";

        /// <summary>
        /// Rotary delta geometry
        /// </summary>
        public const string RotaryDelta = "Rotary delta";

        /// <summary>
        /// SCARA geometry
        /// </summary>
        public const string Scara = "Scara";
    }
}
