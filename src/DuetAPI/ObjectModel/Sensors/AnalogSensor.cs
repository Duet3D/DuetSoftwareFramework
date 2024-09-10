namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Representation of an analog sensor
    /// </summary>
    public partial class AnalogSensor : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Beta value of this sensor (if applicable)
        /// </summary>
        public float? Beta
        {
            get => _beta;
            set => SetPropertyValue(ref _beta, value);
        }
        private float? _beta;

        /// <summary>
        /// C value of this sensor
        /// </summary>
        public float? C
        {
            get => _c;
            set => SetPropertyValue(ref _c, value);
        }
        private float? _c;

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
        public string? Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string? _name;

        /// <summary>
        /// Offset adjustment (in K)
        /// </summary>
        /// <remarks>
        /// See also M308 U
        /// </remarks>
        public float OffsetAdj
        {
            get => _offsetAdj;
            set => SetPropertyValue(ref _offsetAdj, value);
        }
        private float _offsetAdj = 0F;

        /// <summary>
        /// Port of this sensor or null if not applicable
        /// </summary>
        public string? Port
        {
            get => _port;
			set => SetPropertyValue(ref _port, value);
        }
        private string? _port;

        /// <summary>
        /// Resistance of this sensor at 25C
        /// </summary>
        public float? R25
        {
            get => _r25;
            set => SetPropertyValue(ref _r25, value);
        }
        private float? _r25;

        /// <summary>
        /// Series resistance of this sensor channel (only applicable for thermistors)
        /// </summary>
        public float? RRef
        {
            get => _rRef;
            set => SetPropertyValue(ref _rRef, value);
        }
        private float? _rRef;

        /// <summary>
        /// Slope adjustment factor
        /// </summary>
        /// <remarks>
        /// See also M308 V
        /// </remarks>
        public float SlopeAdj
        {
            get => _slopeAdj;
            set => SetPropertyValue(ref _slopeAdj, value);
        }
        private float _slopeAdj = 0F;

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
