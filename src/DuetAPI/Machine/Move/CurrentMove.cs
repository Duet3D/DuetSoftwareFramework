namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current move
    /// </summary>
    public sealed class CurrentMove : ModelObject
    {
        /// <summary>
        /// Acceleration of the current move (in mm/s^2)
        /// </summary>
        public float Acceleration
        {
            get => _acceleration;
			set => SetPropertyValue(ref _acceleration, value);
        }
        private float _acceleration;

        /// <summary>
        /// Deceleration of the current move (in mm/s^2)
        /// </summary>
        public float Deceleration
        {
            get => _deceleration;
			set => SetPropertyValue(ref _deceleration, value);
        }
        private float _deceleration;

        /// <summary>
        /// Laser PWM of the current move (0..1) or null if not applicable
        /// </summary>
        public float? LaserPwm
        {
            get => _laserPwm;
            set => SetPropertyValue(ref _laserPwm, value);
        }
        private float? _laserPwm = null;

        /// <summary>
        /// Requested speed of the current move (in mm/s)
        /// </summary>
        public float RequestedSpeed
        {
            get => _requestedSpeed;
			set => SetPropertyValue(ref _requestedSpeed, value);
        }
        private float _requestedSpeed;
        
        /// <summary>
        /// Top speed of the current move (in mm/s)
        /// </summary>
        public float TopSpeed
        {
            get => _topSpeed;
			set => SetPropertyValue(ref _topSpeed, value);
        }
        private float _topSpeed;
    }
}