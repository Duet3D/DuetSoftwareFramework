namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing information about an attached fan
    /// </summary>
    public sealed class Fan : ModelObject
    {
        /// <summary>
        /// Value of this fan (0..1 or -1 if unknown)
        /// </summary>
        public float ActualValue
        {
            get => _actualValue;
			set => SetPropertyValue(ref _actualValue, value);
        }
        private float _actualValue;

        /// <summary>
        /// Blip value indicating how long the fan is supposed to run at 100% when turning it on to get it started (in s)
        /// </summary>
        public float Blip
        {
            get => _blip;
			set => SetPropertyValue(ref _blip, value);
        }
        private float _blip = 0.1F;

        /// <summary>
        /// Configured frequency of this fan (in Hz)
        /// </summary>
        public float Frequency
        {
            get => _frequency;
			set => SetPropertyValue(ref _frequency, value);
        }
        private float _frequency = 250;

        /// <summary>
        /// Maximum value of this fan (0..1)
        /// </summary>
        public float Max
        {
            get => _max;
			set => SetPropertyValue(ref _max, value);
        }
        private float _max = 1F;

        /// <summary>
        /// Minimum value of this fan (0..1)
        /// </summary>
        public float Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private float _min;

        /// <summary>
        /// Name of the fan
        /// </summary>
        public string Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string _name = string.Empty;

        /// <summary>
        /// Requested value for this fan on a scale between 0 to 1
        /// </summary>
        public float RequestedValue
        {
            get => _requestedValue;
			set => SetPropertyValue(ref _requestedValue, value);
        }
        private float _requestedValue;
        
        /// <summary>
        /// Current RPM of this fan or -1 if unknown/unset
        /// </summary>
        public int Rpm
        {
            get => _rpm;
			set => SetPropertyValue(ref _rpm, value);
        }
        private int _rpm = -1;
        
        /// <summary>
        /// Thermostatic control parameters
        /// </summary>
        public FanThermostaticControl Thermostatic { get; } = new FanThermostaticControl();
    }
}