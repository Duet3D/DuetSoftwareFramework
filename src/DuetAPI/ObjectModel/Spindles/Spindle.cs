namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a CNC spindle
    /// </summary>
    public sealed class Spindle : ModelObject
    {
        /// <summary>
        /// Active RPM
        /// </summary>
        public int Active
        {
            get => _active;
			set => SetPropertyValue(ref _active, value);
        }
        private int _active;

        /// <summary>
        /// Flags whether the spindle may spin in reverse direction
        /// </summary>
        public bool CanReverse
        {
            get => _canReverse;
            set => SetPropertyValue(ref _canReverse, value);
        }
        private bool _canReverse;

        /// <summary>
        /// Current RPM, negative if anticlockwise direction
        /// </summary>
        public int Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private int _current;

        /// <summary>
        /// Frequency (in Hz)
        /// </summary>
        public int Frequency
        {
            get => _frequency;
			set => SetPropertyValue(ref _frequency, value);
        }
        private int _frequency;

        /// <summary>
        /// Idle PWM value (0..1)
        /// </summary>
        public float IdlePwm
        {
            get => _idlePwm;
            set => SetPropertyValue(ref _idlePwm, value);
        }
        private float _idlePwm;

        /// <summary>
        /// Maximum RPM
        /// </summary>
        public int Max
        {
            get => _max;
            set => SetPropertyValue(ref _max, value);
        }
        private int _max = 10000;

        /// <summary>
        /// Maximum PWM value when turned on (0..1)
        /// </summary>
        public float MaxPwm
        {
            get => _maxPwm;
            set => SetPropertyValue(ref _maxPwm, value);
        }
        private float _maxPwm = 1F;

        /// <summary>
        /// Minimum RPM when turned on
        /// </summary>
        public int Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private int _min = 60;

        /// <summary>
        /// Minimum PWM value when turned on (0..1)
        /// </summary>
        public float MinPwm
        {
            get => _minPwm;
            set => SetPropertyValue(ref _minPwm, value);
        }
        private float _minPwm;

        /// <summary>
        /// Current state
        /// </summary>
        public SpindleState State
        {
            get => _state;
            set => SetPropertyValue(ref _state, value);
        }
        private SpindleState _state = SpindleState.Unconfigured;
    }
}