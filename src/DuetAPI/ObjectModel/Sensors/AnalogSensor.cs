namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Representation of an analog sensor
    /// </summary>
    public sealed class AnalogSensor : ModelObject
    {
        /// <summary>
        /// Last sensor reading (in C) or null if invalid
        /// </summary>
        public float? LastReading
        {
            get => _lastReading;
			set => SetPropertyValue(ref _lastReading, value);
        }
        private float? _lastReading;

        /// <summary>
        /// Name of this sensor or null if not configured
        /// </summary>
        public string Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string _name;

        /// <summary>
        /// State of this sensor
        /// </summary>
        public TemperatureError State
        {
            get => _state;
            set => SetPropertyValue(ref _state, value);
        }
        private TemperatureError _state = TemperatureError.Ok;

        /// <summary>
        /// Type of this sensor
        /// </summary>
        public AnalogSensorType Type
        {
            get => _type;
			set => SetPropertyValue(ref _type, value);
        }
        private AnalogSensorType _type = AnalogSensorType.Unknown;
    }
}
