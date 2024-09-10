using DuetAPI.Utility;
using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a configured axis
    /// </summary>
    public partial class Axis : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// List of supported axis letters
        /// </summary>
        public static readonly char[] Letters = [
            'X', 'Y', 'Z',
            'U', 'V', 'W',
            'A', 'B', 'C', 'D',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z'
        ];

        /// <summary>
        /// Acceleration of this axis (in mm/s^2)
        /// </summary>
        public float Acceleration
        {
            get => _acceleration;
			set => SetPropertyValue(ref _acceleration, value);
        }
        private float _acceleration;

        /// <summary>
        /// Babystep amount (in mm)
        /// </summary>
        public float Babystep
        {
            get => _babystep;
			set => SetPropertyValue(ref _babystep, value);
        }
        private float _babystep;

        /// <summary>
        /// Configured backlash of this axis (in mm)
        /// </summary>
        public float Backlash
        {
            get => _backlash;
            set => SetPropertyValue(ref _backlash, value);
        }
        private float _backlash;

        /// <summary>
        /// Motor current (in mA)
        /// </summary>
        public int Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private int _current;

        /// <summary>
        /// List of the assigned drivers
        /// </summary>
        public ObservableCollection<DriverId> Drivers { get; } = [];

        /// <summary>
        /// Whether or not the axis is homed
        /// </summary>
        public bool Homed
        {
            get => _homed;
			set => SetPropertyValue(ref _homed, value);
        }
        private bool _homed;

        /// <summary>
        /// Motor jerk (in mm/min)
        /// </summary>
        public float Jerk
        {
            get => _jerk;
			set => SetPropertyValue(ref _jerk, value);
        }
        private float _jerk = 15F;

        /// <summary>
        /// Letter of this axis
        /// </summary>
        public char Letter
        {
            get => _letter;
			set => SetPropertyValue(ref _letter, value);
        }
        private char _letter;

        /// <summary>
        /// Current machine position (in mm) or null if unknown/unset
        /// </summary>
        /// <remarks>
        /// This value reflects the machine position of the move being performed or of the last one if the machine is not moving
        /// </remarks>
        public float? MachinePosition
        {
            get => _machinePosition;
			set => SetPropertyValue(ref _machinePosition, value);
        }
        private float? _machinePosition;

        /// <summary>
        /// Maximum travel of this axis (in mm)
        /// </summary>
        public float Max
        {
            get => _max;
			set => SetPropertyValue(ref _max, value);
        }
        private float _max = 200F;

        /// <summary>
        /// Whether the axis maximum was probed
        /// </summary>
        public bool MaxProbed
        {
            get => _maxProbed;
			set => SetPropertyValue(ref _maxProbed, value);
        }
        private bool _maxProbed;

        /// <summary>
        /// Microstepping configuration
        /// </summary>
        public Microstepping Microstepping { get; } = new Microstepping();

        /// <summary>
        /// Minimum travel of this axis (in mm)
        /// </summary>
        public float Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private float _min;

        /// <summary>
        /// Whether the axis minimum was probed
        /// </summary>
        public bool MinProbed
        {
            get => _minProbed;
			set => SetPropertyValue(ref _minProbed, value);
        }
        private bool _minProbed;

        /// <summary>
        /// Percentage applied to the motor current (0..100)
        /// </summary>
        public int PercentCurrent
        {
            get => _percentCurrent;
            set => SetPropertyValue(ref _percentCurrent, value);
        }
        private int _percentCurrent = 100;

        /// <summary>
        /// Percentage applied to the motor current during standstill (0..100 or null if not supported)
        /// </summary>
        public int? PercentStstCurrent
        {
            get => _percentStstCurrent;
            set => SetPropertyValue(ref _percentStstCurrent, value);
        }
        private int? _percentStstCurrent;

        /// <summary>
        /// Reduced accelerations used by Z probing and stall homing moves (in mm/s^2)
        /// </summary>
        public float ReducedAcceleration
        {
            get => _reducedAcceleration;
			set => SetPropertyValue(ref _reducedAcceleration, value);
        }
        private float _reducedAcceleration;

        /// <summary>
        /// Maximum speed (in mm/min)
        /// </summary>
        public float Speed
        {
            get => _speed;
			set => SetPropertyValue(ref _speed, value);
        }
        private float _speed = 100F;

        /// <summary>
        /// Number of microsteps per mm
        /// </summary>
        public float StepsPerMm
        {
            get => _stepsPerMm;
            set => SetPropertyValue(ref _stepsPerMm, value);
        }
        private float _stepsPerMm = 80F;

        /// <summary>
        /// Current user position (in mm) or null if unknown
        /// </summary>
        /// <remarks>
        /// This value reflects the target position of the last move fed into the look-ahead buffer
        /// </remarks>
        public float? UserPosition
        {
            get => _userPosition;
			set => SetPropertyValue(ref _userPosition, value);
        }
        private float? _userPosition;

        /// <summary>
        /// Whether or not the axis is visible
        /// </summary>
        public bool Visible
        {
            get => _visible;
			set => SetPropertyValue(ref _visible, value);
        }
        private bool _visible = true;

        /// <summary>
        /// Offsets of this axis for each workplace (in mm)
        /// </summary>
        public ObservableCollection<float> WorkplaceOffsets { get; } = [];
    }
}
