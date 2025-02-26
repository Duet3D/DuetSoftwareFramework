namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Kinematics class for polar kinematics
    /// </summary>
    public partial class PolarKinematics : Kinematics
    {
        /// <summary>
        /// Homed radius (in mm)
        /// </summary>
        public float RadiusHomed
        {
            get => _radiusHomed;
            set => SetPropertyValue(ref _radiusHomed, value);
        }
        private float _radiusHomed;

        /// <summary>
        /// Maximum radius (in mm)
        /// </summary>
        public float RadiusMax
        {
            get => _radiusMax;
            set => SetPropertyValue(ref _radiusMax, value);
        }
        private float _radiusMax;

        /// <summary>
        /// Minimum radius (in mm)
        /// </summary>
        public float RadiusMin
        {
            get => _radiusMin;
            set => SetPropertyValue(ref _radiusMin, value);
        }
        private float _radiusMin;

        /// <summary>
        /// Maximum turntable acceleration (in mm/s^2)
        /// </summary>
        public float TTAccMax
        {
            get => _ttAccMax;
            set => SetPropertyValue(ref _ttAccMax, value);
        }
        private float _ttAccMax;

        /// <summary>
        /// Maximum turntable speed (in mm/s)
        /// </summary>
        public float TTSpeedMax
        {
            get => _ttSpeedMax;
            set => SetPropertyValue(ref _ttSpeedMax, value);
        }
        private float _ttSpeedMax;
    }
}
