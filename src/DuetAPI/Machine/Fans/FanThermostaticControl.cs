namespace DuetAPI.Machine
{
    /// <summary>
    /// Thermostatic parameters of a fan
    /// </summary>
    public sealed class FanThermostaticControl : ModelObject
    {
        /// <summary>
        /// Defines whether thermostatic control is enabled
        /// </summary>
        public bool Control
        {
            get => _control;
			set => SetPropertyValue(ref _control, value);
        }
        private bool _control;

        /// <summary>
        /// List of the heaters to monitor (indices)
        /// </summary>
        public ModelCollection<int> Heaters { get; } = new ModelCollection<int>();
        
        /// <summary>
        /// Upper temperature range required to turn on the fan on (in C)
        /// </summary>
        public float HighTemperature
        {
            get => _highTemperature;
			set => SetPropertyValue(ref _highTemperature, value);
        }
        private float _highTemperature;

        /// <summary>
        /// Lower temperature range required to turn on the fan on (in C)
        /// </summary>
        public float LowTemperature
        {
            get => _lowTemperature;
			set => SetPropertyValue(ref _lowTemperature, value);
        }
        private float _lowTemperature;
    }
}