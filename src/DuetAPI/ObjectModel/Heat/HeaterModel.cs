namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the way the heater heats up
    /// </summary>
    public sealed class HeaterModel : ModelObject
    {
        /// <summary>
        /// Cooling rate exponent
        /// </summary>
        public float CoolingExp
        {
            get => _coolingExp;
            set => SetPropertyValue(ref _coolingExp, value);
        }
        private float _coolingExp = 1.35F;

        /// <summary>
        /// Cooling rate (in K/s)
        /// </summary>
        public float CoolingRate
        {
            get => _coolingRate;
            set => SetPropertyValue(ref _coolingRate, value);
        }
        private float _coolingRate = 0.56F;

        /// <summary>
        /// Dead time (in s)
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
        /// Cooling rate with the fan on (in K/s)
        /// </summary>
        public float FanCoolingRate
        {
            get => _fanCoolingRate;
            set => SetPropertyValue(ref _fanCoolingRate, value);
        }
        private float _fanCoolingRate = 0.56F;

        /// <summary>
        /// Heating rate (in K/s)
        /// </summary>
        public float HeatingRate
        {
            get => _heatingRate;
            set => SetPropertyValue(ref _heatingRate, value);
        }
        private float _heatingRate = 2.43F;

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
        public float StandardVoltage
        {
            get => _standardVoltage;
			set => SetPropertyValue(ref _standardVoltage, value);
        }
        private float _standardVoltage;
    }
}