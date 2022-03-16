namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Details about the PID model of a heater
    /// </summary>
    public sealed class HeaterModelPID : ModelObject
    {
        /// <summary>
        /// Derivative value of the PID regulator
        /// </summary>
        public float D
        {
            get => _d;
            set => SetPropertyValue(ref _d, value);
        }
        private float _d;

        /// <summary>
        /// Integral value of the PID regulator
        /// </summary>
        public float I
        {
            get => _i;
            set => SetPropertyValue(ref _i, value);
        }
        private float _i;

        /// <summary>
        /// Indicates if custom PID values are used
        /// </summary>
        public bool Overridden
        {
            get => _overridden;
			set => SetPropertyValue(ref _overridden, value);
        }
        private bool _overridden;

        /// <summary>
        /// Proportional value of the PID regulator
        /// </summary>
        public float P
        {
            get => _p;
			set => SetPropertyValue(ref _p, value);
        }
        private float _p;

        /// <summary>
        /// Indicates if PID control is being used
        /// </summary>
        public bool Used
        {
            get => _used;
			set => SetPropertyValue(ref _used, value);
        }
        private bool _used = true;
    }
}
