namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current job
    /// </summary>
    public sealed class Job : ModelObject
    {
        /// <summary>
        /// Information about the current build or null if not available
        /// </summary>
        public Build Build
        {
            get => _build;
            set => SetPropertyValue(ref _build, value);
        }
        private Build _build;

        /// <summary>
        /// Total duration of the current job (in s or null)
        /// </summary>
        public float? Duration
        {
            get => _duration;
			set => SetPropertyValue(ref _duration, value);
        }
        private float? _duration;

        /// <summary>
        /// Information about the file being processed
        /// </summary>
        public ParsedFileInfo File { get; } = new ParsedFileInfo();
        
        /// <summary>
        /// Current position in the file being processed (in bytes or null)
        /// </summary>
        public long? FilePosition
        {
            get => _filePosition;
			set => SetPropertyValue(ref _filePosition, value);
        }
        private long? _filePosition;

        /// <summary>
        /// Duration of the first layer (in s or null)
        /// </summary>
        public float? FirstLayerDuration
        {
            get => _firstLayerDuration;
			set => SetPropertyValue(ref _firstLayerDuration, value);
        }
        private float? _firstLayerDuration;

        /// <summary>
        /// Name of the last file processed or null if none
        /// </summary>
        public string LastFileName
        {
            get => _lastFileName;
			set => SetPropertyValue(ref _lastFileName, value);
        }
        private string _lastFileName;

        /// <summary>
        /// Indicates if the last file was aborted (unexpected cancellation)
        /// </summary>
        public bool LastFileAborted
        {
            get => _lastFileAborted;
			set => SetPropertyValue(ref _lastFileAborted, value);
        }
        private bool _lastFileAborted;
        
        /// <summary>
        /// Indicates if the last file was cancelled (user cancelled)
        /// </summary>
        public bool LastFileCancelled
        {
            get => _lastFileCancelled;
			set => SetPropertyValue(ref _lastFileCancelled, value);
        }
        private bool _lastFileCancelled;

        /// <summary>
        /// Indicates if the last file processed was simulated
        /// </summary>
        /// <remarks>This is not set if the file was aborted or cancelled</remarks>
        public bool LastFileSimulated
        {
            get => _lastFileSimulated;
			set => SetPropertyValue(ref _lastFileSimulated, value);
        }
        private bool _lastFileSimulated;

        /// <summary>
        /// Number of the current layer or null not available
        /// </summary>
        public int? Layer
        {
            get => _layer;
			set => SetPropertyValue(ref _layer, value);
        }
        private int? _layer;
        
        /// <summary>
        /// Time elapsed since the beginning of the current layer (in s or null)
        /// </summary>
        public float? LayerTime
        {
            get => _layerTime;
			set => SetPropertyValue(ref _layerTime, value);
        }
        private float? _layerTime;
        
        /// <summary>
        /// Information about the past layers
        /// </summary>
        /// <seealso cref="Layer"/>
        public ModelGrowingCollection<Layer> Layers { get; } = new ModelGrowingCollection<Layer>();
        
        /// <summary>
        /// Estimated times left
        /// </summary>
        public TimesLeft TimesLeft { get; } = new TimesLeft();

        /// <summary>
        /// Time needed to heat up the heaters (in s or null)
        /// </summary>
        public float? WarmUpDuration
        {
            get => _warmUpDuration;
			set => SetPropertyValue(ref _warmUpDuration, value);
        }
        private float? _warmUpDuration;
    }
}
