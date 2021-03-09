using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a configured probe
    /// </summary>
    public sealed class Probe : ModelObject
    {
        /// <summary>
        /// Calibration temperature (in C)
        /// </summary>
        public float CalibrationTemperature
        {
            get => _calibrationTemperature;
			set => SetPropertyValue(ref _calibrationTemperature, value);
        }
        private float _calibrationTemperature;

        /// <summary>
        /// Indicates if the user has deployed the probe
        /// </summary>
        public bool DeployedByUser
        {
            get => _deployedByUser;
			set => SetPropertyValue(ref _deployedByUser, value);
        }
        private bool _deployedByUser;

        /// <summary>
        /// Whether probing disables the heater(s)
        /// </summary>
        public bool DisablesHeaters
        {
            get => _disablesHeaters;
			set => SetPropertyValue(ref _disablesHeaters, value);
        }
        private bool _disablesHeaters;

        /// <summary>
        /// Dive height (in mm)
        /// </summary>
        public float DiveHeight
        {
            get => _diveHeight;
			set => SetPropertyValue(ref _diveHeight, value);
        }
        private float _diveHeight;

        /// <summary>
        /// Height of the probe where it stopped last time (in mm)
        /// </summary>
        public float LastStopHeight
        {
            get => _lastStopHeight;
            set => SetPropertyValue(ref _lastStopHeight, value);
        }
        private float _lastStopHeight;

        /// <summary>
        /// Maximum number of times to probe after a bad reading was determined
        /// </summary>
        public int MaxProbeCount
        {
            get => _maxProbeCount;
			set => SetPropertyValue(ref _maxProbeCount, value);
        }
        private int _maxProbeCount = 1;

        /// <summary>
        /// X+Y offsets (in mm)
        /// </summary>
        public ModelCollection<float> Offsets { get; } = new ModelCollection<float>() { 0F, 0F };

        /// <summary>
        /// Recovery time (in s)
        /// </summary>
        public float RecoveryTime
        {
            get => _recoveryTime;
			set => SetPropertyValue(ref _recoveryTime, value);
        }
        private float _recoveryTime;

        /// <summary>
        /// Probe speed (in mm/s)
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use Speeds[0] instead")]
        public float Speed
        {
            get => Speeds[0];
            set => Speeds[0] = value;
        }

        /// <summary>
        /// Fast and slow probing speeds (in mm/s)
        /// </summary>
        public ModelCollection<float> Speeds { get; } = new ModelCollection<float>() { 2F, 2F };

        /// <summary>
        /// First temperature coefficient
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use TemperatureCoefficients instead")]
        public float TemperatureCoefficient
        {
            get => TemperatureCoefficients[0];
            set => TemperatureCoefficients[0] = value;
        }

        /// <summary>
        /// List of temperature coefficients
        /// </summary>
        public ModelCollection<float> TemperatureCoefficients { get; } = new ModelCollection<float>() { 0F, 0F };

        /// <summary>
        /// Configured trigger threshold (0..1023)
        /// </summary>
        public int Threshold
        {
            get => _threshold;
			set => SetPropertyValue(ref _threshold, value);
        }
        private int _threshold = 500;

        /// <summary>
        /// Allowed tolerance deviation between two measures (in mm)
        /// </summary>
        public float Tolerance
        {
            get => _tolerance;
			set => SetPropertyValue(ref _tolerance, value);
        }
        private float _tolerance = 0.03F;

        /// <summary>
        /// Travel speed when probing multiple points (in mm/s)
        /// </summary>
        public float TravelSpeed
        {
            get => _travelSpeed;
			set => SetPropertyValue(ref _travelSpeed, value);
        }
        private float _travelSpeed = 100F;

        /// <summary>
        /// Z height at which the probe is triggered (in mm)
        /// </summary>
        public float TriggerHeight
        {
            get => _triggerHeight;
			set => SetPropertyValue(ref _triggerHeight, value);
        }
        private float _triggerHeight = 0.7F;

        /// <summary>
        /// Type of the configured probe
        /// </summary>
        /// <seealso cref="ProbeType"/>
        public ProbeType Type
        {
            get => _type;
			set => SetPropertyValue(ref _type, value);
        }
        private ProbeType _type = ProbeType.None;

        /// <summary>
        /// Current analog values of the probe
        /// </summary>
        public ModelCollection<int> Value { get; } = new ModelCollection<int>();
    }
}