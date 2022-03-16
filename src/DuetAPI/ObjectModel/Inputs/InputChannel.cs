namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a G/M/T-code channel
    /// </summary>
    public sealed class InputChannel : ModelObject
    {
        /// <summary>
        /// Whether relative positioning is being used
        /// </summary>
        public bool AxesRelative
        {
            get => _axesRelative;
            set => SetPropertyValue(ref _axesRelative, value);
        }
        private bool _axesRelative = false;

        /// <summary>
        /// Emulation used on this channel
        /// </summary>
        public Compatibility Compatibility
        {
            get => _compatibility;
            set => SetPropertyValue(ref _compatibility, value);
        }
        private Compatibility _compatibility = Compatibility.RepRapFirmware;

        /// <summary>
        /// Whether inches are being used instead of mm
        /// </summary>
        public DistanceUnit DistanceUnit
        {
            get => _distanceUnit;
            set => SetPropertyValue(ref _distanceUnit, value);
        }
        private DistanceUnit _distanceUnit = DistanceUnit.MM;

        /// <summary>
        /// Whether relative extrusion is being used
        /// </summary>
        public bool DrivesRelative
        {
            get => _drivesRelative;
            set => SetPropertyValue(ref _drivesRelative, value);
        }
        private bool _drivesRelative = true;

        /// <summary>
        /// Current feedrate in mm/s
        /// </summary>
        public float FeedRate
        {
            get => _feedRate;
            set => SetPropertyValue(ref _feedRate, value);
        }
        private float _feedRate = 50.0F;

        /// <summary>
        /// Whether a macro file is being processed
        /// </summary>
        public bool InMacro
        {
            get => _inMacro;
            set => SetPropertyValue(ref _inMacro, value);
        }
        private bool _inMacro = false;

        /// <summary>
        /// Indicates if the current macro file can be restarted after a pause
        /// </summary>
        public bool MacroRestartable
        {
            get => _macroRestartable;
            set => SetPropertyValue(ref _macroRestartable, value);
        }
        private bool _macroRestartable;

        /// <summary>
        /// Name of this channel
        /// </summary>
        public CodeChannel Name
        {
            get => _name;
            set => SetPropertyValue(ref _name, value);
        }
        private CodeChannel _name = CodeChannel.Unknown;

        /// <summary>
        /// Depth of the stack
        /// </summary>
        public byte StackDepth
        {
            get => _stackDepth;
            set => SetPropertyValue(ref _stackDepth, value);
        }
        private byte _stackDepth = 0;

        /// <summary>
        /// State of this input channel
        /// </summary>
        public InputChannelState State
        {
            get => _state;
            set => SetPropertyValue(ref _state, value);
        }
        private InputChannelState _state = InputChannelState.Idle;

        /// <summary>
        /// Number of the current line
        /// </summary>
        public long LineNumber
        {
            get => _lineNumber;
            set => SetPropertyValue(ref _lineNumber, value);
        }
        private long _lineNumber = 0;

        /// <summary>
        /// Whether volumetric extrusion is being used
        /// </summary>
        public bool Volumetric
        {
            get => _volumetric;
            set => SetPropertyValue(ref _volumetric, value);
        }
        private bool _volumetric = false;
    }
}
