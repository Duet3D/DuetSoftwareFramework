namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a heater
    /// </summary>
    public partial class Heater : ModelObject
    {
        /// <summary>
        /// Active temperature of the heater (in C)
        /// </summary>
        public float Active
        {
            get => _active;
			set => SetPropertyValue(ref _active, value);
        }
        private float _active;

        /// <summary>
        /// Average heater PWM value (0..1)
        /// </summary>
        public float AvgPwm
        {
            get => _avgPwm;
            set => SetPropertyValue(ref _avgPwm, value);
        }
        private float _avgPwm;

        /// <summary>
        /// Current temperature of the heater (in C)
        /// </summary>
        public float Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private float _current = -273.15F;

        /// <summary>
        /// Maximum temperature allowed for this heater (in C)
        /// </summary>
        /// <remarks>
        /// This is only temporary and should be replaced by a representation of the heater protection as in RRF
        /// </remarks>
        public float Max
        {
            get => _max;
			set => SetPropertyValue(ref _max, value);
        }
        private float _max = 285F;

        /// <summary>
        /// Minimum temperature allowed for this heater (in C)
        /// </summary>
        /// <remarks>
        /// This is only temporary and should be replaced by a representation of the heater protection as in RRF
        /// </remarks>
        public float Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private float _min = -10F;

        /// <summary>
        /// Maximum number of consecutive temperature reading failures before a heater fault is raised
        /// </summary>
        public int MaxBadReadings
        {
            get => _maxBadReadings;
            set => SetPropertyValue(ref _maxBadReadings, value);
        }
        private int _maxBadReadings = 3;

        /// <summary>
        /// Time for which a temperature anomaly must persist on this heater before raising a heater fault (in s)
        /// </summary>
        public float MaxHeatingFaultTime
        {
            get => _maxHeatingFaultTime;
            set => SetPropertyValue(ref _maxHeatingFaultTime, value);
        }
        private float _maxHeatingFaultTime = 5F;

        /// <summary>
        /// Permitted temperature excursion from the setpoint for this heater (in K)
        /// </summary>
        public float MaxTempExcursion
        {
            get => _maxTempExcursion;
            set => SetPropertyValue(ref _maxTempExcursion, value);
        }
        private float _maxTempExcursion = 15F;

        /// <summary>
        /// Information about the heater model
        /// </summary>
        public HeaterModel Model { get; } = new HeaterModel();

        /// <summary>
        /// Monitors of this heater
        /// </summary>
        public ModelCollection<HeaterMonitor> Monitors { get; } = [];

        /// <summary>
        /// Sensor number of this heater or -1 if not configured
        /// </summary>
        public int Sensor
        {
            get => _sensor;
			set => SetPropertyValue(ref _sensor, value);
        }
        private int _sensor = -1;

        /// <summary>
        /// Standby temperature of the heater (in C)
        /// </summary>
        public float Standby
        {
            get => _standby;
			set => SetPropertyValue(ref _standby, value);
        }
        private float _standby;

        /// <summary>
        /// State of the heater
        /// </summary>
        public HeaterState State
        {
            get => _state;
			set => SetPropertyValue(ref _state, value);
        }
        private HeaterState _state = HeaterState.Off;
    }
}