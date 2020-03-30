namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a configured tool
    /// </summary>
    public sealed class Tool : ModelObject
    {
        /// <summary>
        /// Active temperatures of the associated heaters (in C)
        /// </summary>
        public ModelCollection<float> Active { get; } = new ModelCollection<float>();

        /// <summary>
        /// Associated axes. At present only X and Y can be mapped per tool.
        /// </summary>
        /// <remarks>
        /// The order is the same as the visual axes, so by default the layout is
        /// [
        ///   [0],        // X
        ///   [1]         // Y
        /// ]
        /// Make sure to set each item individually so the change events are called
        /// </remarks>
        public ModelCollection<int[]> Axes { get; } = new ModelCollection<int[]>();

        /// <summary>
        /// Extruder drives of this tool
        /// </summary>
        public ModelCollection<int> Extruders { get; } = new ModelCollection<int>();

        /// <summary>
        /// List of associated fans (indices)
        /// </summary>
        public ModelCollection<int> Fans { get; } = new ModelCollection<int>();
        
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
        public ModelCollection<int> Heaters { get; } = new ModelCollection<int>();

        /// <summary>
        /// Mix ratios of the associated extruder drives
        /// </summary>
        public ModelCollection<float> Mix { get; } = new ModelCollection<float>();
        
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
        public ModelCollection<float> Offsets { get; } = new ModelCollection<float>();

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
        /// Standby temperatures of the associated heaters (in C)
        /// </summary>
        public ModelCollection<float> Standby { get; } = new ModelCollection<float>();

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