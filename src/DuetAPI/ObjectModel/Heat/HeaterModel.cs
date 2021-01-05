namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the way the heater heats up
    /// </summary>
    public sealed class HeaterModel : ModelObject
    {
        /// <summary>
        /// Dead time
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
        /// Gain value
        /// </summary>
        public float Gain
        {
            get => _gain;
			set => SetPropertyValue(ref _gain, value);
        }
        private float _gain = 340F;

        /// <summary>
        /// Heating rate (in K/s)
        /// </summary>
        public float HeatingRate
        {
            get => _heatingRate;
            set => SetPropertyValue(ref _heatingRate, value);
        }
        private float _heatingRate;

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
        /// Maximum PWM value
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
        /// Standard voltage or null if unknown
        /// </summary>
        public float? StandardVoltage
        {
            get => _standardVoltage;
			set => SetPropertyValue(ref _standardVoltage, value);
        }
        private float? _standardVoltage;

        /// <summary>
        /// Time constant
        /// </summary>
        public float TimeConstant
        {
            get => _timeConstant;
			set => SetPropertyValue(ref _timeConstant, value);
        }
        private float _timeConstant = 140F;

        /// <summary>
        /// Time constant with the fans on
        /// </summary>
        public float TimeConstantFansOn
        {
            get => _timeConstantFanOn;
            set => SetPropertyValue(ref _timeConstantFanOn, value);
        }
        private float _timeConstantFanOn = 140F;
    }
}