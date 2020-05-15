namespace DuetAPI.Machine
{
    /// <summary>
    /// Delta kinematics
    /// </summary>
    public sealed class DeltaKinematics : Kinematics
    {
        /// <summary>
        /// Delta radius (in mm)
        /// </summary>
        public float DeltaRadius
        {
            get => _deltaRadius;
			set => SetPropertyValue(ref _deltaRadius, value);
        }
        private float _deltaRadius;

        /// <summary>
        /// Homed height of a delta printer in mm
        /// </summary>
        public float HomedHeight
        {
            get => _homedHeight;
			set => SetPropertyValue(ref _homedHeight, value);
        }
        private float _homedHeight;

        /// <summary>
        /// Print radius for Hangprinter and Delta geometries (in mm)
        /// </summary>
        public float PrintRadius
        {
            get => _printRadius;
			set => SetPropertyValue(ref _printRadius, value);
        }
        private float _printRadius;

        /// <summary>
        /// Delta tower properties
        /// </summary>
        public ModelCollection<DeltaTower> Towers { get; } = new ModelCollection<DeltaTower>();

        /// <summary>
        /// How much Z needs to be raised for each unit of movement in the +X direction
        /// </summary>
        public float XTilt
        {
            get => _xTilt;
			set => SetPropertyValue(ref _xTilt, value);
        }
        private float _xTilt;

        /// <summary>
        /// How much Z needs to be raised for each unit of movement in the +Y direction
        /// </summary>
        public float YTilt
        {
            get => _yTilt;
			set => SetPropertyValue(ref _yTilt, value);
        }
        private float _yTilt;
    }
}
