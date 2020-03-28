namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the way the heater heats up
    /// </summary>
    public sealed class HeaterModel : ModelObject
    {
        /// <summary>
        /// Dead time of this heater or null if unknown
        /// </summary>
        public float DeadTime
        {
            get => _deadTime;
			set => SetPropertyValue(ref _deadTime, value);
        }
        private float _deadTime = 5.5F;

        /// <summary>
        /// Indicates if this heater is enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
			set => SetPropertyValue(ref _enabled, value);
        }
        private bool _enabled;

        /// <summary>
        /// Gain value or null if unknown
        /// </summary>
        public float Gain
        {
            get => _gain;
			set => SetPropertyValue(ref _gain, value);
        }
        private float _gain = 340F;

        /// <summary>
        /// Indicates if the heater PWM signal is inverted
        /// </summary>
        public bool Inverted
        {
            get => _inverted;
			set => SetPropertyValue(ref _inverted, value);
        }
        private bool _inverted;

        /// <summary>
        /// Maximum PWM or null if unknown
        /// </summary>
        public float MaxPwm
        {
            get => _maxPwm;
			set => SetPropertyValue(ref _maxPwm, value);
        }
        private float _maxPwm = 1F;

        /// <summary>
        /// Details about the PID controller
        /// </summary>
        public HeaterModelPID PID { get; } = new HeaterModelPID();

        /// <summary>
        /// Standard voltage of this heater or null if unknown
        /// </summary>
        public float? StandardVoltage
        {
            get => _standardVoltage;
			set => SetPropertyValue(ref _standardVoltage, value);
        }
        private float? _standardVoltage;

        /// <summary>
        /// Time constant or null if unknown
        /// </summary>
        public float TimeConstant
        {
            get => _timeConstant;
			set => SetPropertyValue(ref _timeConstant, value);
        }
        private float _timeConstant = 140F;
    }
}