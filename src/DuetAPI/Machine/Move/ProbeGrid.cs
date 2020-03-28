using DuetAPI.Utility;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the configured probe grid (see M557)
    /// </summary>
    /// <seealso cref="Heightmap"/>
    public sealed class ProbeGrid : ModelObject
    {
        /// <summary>
        /// X start coordinate of the heightmap
        /// </summary>
        public float XMin
        {
            get => _xMin;
			set => SetPropertyValue(ref _xMin, value);
        }
        private float _xMin;

        /// <summary>
        /// X end coordinate of the heightmap
        /// </summary>
        public float XMax
        {
            get => _xMax;
			set => SetPropertyValue(ref _xMax, value);
        }
        private float _xMax;

        /// <summary>
        /// Spacing between the probe points in X direction
        /// </summary>
        public float XSpacing
        {
            get => _xSpacing;
			set => SetPropertyValue(ref _xSpacing, value);
        }
        private float _xSpacing;

        /// <summary>
        /// Y start coordinate of the heightmap
        /// </summary>
        public float YMin
        {
            get => _yMin;
			set => SetPropertyValue(ref _yMin, value);
        }
        private float _yMin;

        /// <summary>
        /// Y end coordinate of the heightmap
        /// </summary>
        public float YMax
        {
            get => _yMax;
			set => SetPropertyValue(ref _yMax, value);
         }
        private float _yMax;

        /// <summary>
        /// Spacing between the probe points in Y direction
        /// </summary>
        public float YSpacing
        {
            get => _ySpacing;
			set => SetPropertyValue(ref _ySpacing, value);
        }
        private float _ySpacing;

        /// <summary>
        /// Probing radius for delta kinematics
        /// </summary>
        public float Radius
        {
            get => _radius;
			set => SetPropertyValue(ref _radius, value);
        }
        private float _radius;
    }
}
