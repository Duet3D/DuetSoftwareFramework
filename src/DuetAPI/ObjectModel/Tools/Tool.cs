namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a configured tool
    /// </summary>
    public sealed class Tool : ModelObject
    {
        /// <summary>
        /// Active temperatures of the associated heaters (in C)
        /// </summary>
        public ModelCollection<float> Active { get; } = [];

        /// <summary>
        /// Associated axes. At present only X and Y can be mapped per tool.
        /// </summary>
        /// <remarks>
        /// The order is the same as the visual axes, so by default the layout is
        /// [
        ///   [0],        // X
        ///   [1]         // Y
        /// ]
        /// Make sure to set each item individually so the change events are called.
        /// Each item is a bitmap represented as an array
        /// </remarks>
        public ModelCollection<int[]> Axes { get; } = [];

        /// <summary>
        /// Extruder drives of this tool
        /// </summary>
        public ModelCollection<int> Extruders { get; } = [];

        /// <summary>
        /// List of associated fans (indices)
        /// </summary>
        /// <remarks>
        /// This is a bitmap represented as an array
        /// </remarks>
        public ModelCollection<int> Fans { get; } = [];

        /// <summary>
        /// Feedforward coefficients to apply to the mapped heaters during extrusions
        /// </summary>
        public ModelCollection<float> FeedForward { get; } = [];

        /// <summary>
        /// Extruder drive index for resolving the tool filament (index or -1)
        /// </summary>
        public int FilamentExtruder
        {
            get => _filamentExtruder;
            set => SetPropertyValue(ref _filamentExtruder, value);
        }
        private int _filamentExtruder = -1;

        /// <summary>
        /// List of associated heaters (indices)
        /// </summary>
        public ModelCollection<int> Heaters { get; } = [];

        /// <summary>
        /// True if the filament has been firmware-retracted
        /// </summary>
        public bool IsRetracted
        {
            get => _isRetracted;
            set => SetPropertyValue(ref _isRetracted, value);
        }
        private bool _isRetracted;

        /// <summary>
        /// Mix ratios of the associated extruder drives
        /// </summary>
        public ModelCollection<float> Mix { get; } = [];
        
        /// <summary>
        /// Name of this tool
        /// </summary>
        public string Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string _name = string.Empty;

        /// <summary>
        /// Number of this tool
        /// </summary>
        public int Number
        {
            get => _number;
			set => SetPropertyValue(ref _number, value);
        }
        private int _number;

        /// <summary>
        /// Axis offsets (in mm)
        /// This list is in the same order as <see cref="Move.Axes"/>
        /// </summary>
        /// <seealso cref="Axis"/>
        public ModelCollection<float> Offsets { get; } = [];

        /// <summary>
        /// Bitmap of the probed axis offsets
        /// </summary>
        public int OffsetsProbed
        {
            get => _offsetsProbed;
			set => SetPropertyValue(ref _offsetsProbed, value);
        }
        private int _offsetsProbed;

        /// <summary>
        /// Firmware retraction parameters
        /// </summary>
        public ToolRetraction Retraction { get; } = new ToolRetraction();

        /// <summary>
        /// Index of the mapped spindle or -1 if not mapped
        /// </summary>
        public int Spindle
        {
            get => _spindle;
            set => SetPropertyValue(ref _spindle, value);
        }
        private int _spindle = -1;

        /// <summary>
        /// RPM of the mapped spindle
        /// </summary>
        public int SpindleRpm
        {
            get => _spindleRpm;
            set => SetPropertyValue(ref _spindleRpm, value);
        }
        private int _spindleRpm;

        /// <summary>
        /// Standby temperatures of the associated heaters (in C)
        /// </summary>
        public ModelCollection<float> Standby { get; } = [];

        /// <summary>
        /// Current state of this tool
        /// </summary>
        public ToolState State
        {
            get => _state;
			set => SetPropertyValue(ref _state, value);
        }
        private ToolState _state = ToolState.Off;
    }
}