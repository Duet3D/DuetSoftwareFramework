using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
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
        /// Total active duration of the current job file (in s or null)
        /// </summary>
        public int? Duration
        {
            get => _duration;
			set => SetPropertyValue(ref _duration, value);
        }
        private int? _duration;

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
        [JsonIgnore]
        [Obsolete("No longer used, will always return null")]
        public int? FirstLayerDuration
        {
            get => _firstLayerDuration;
			set => SetPropertyValue(ref _firstLayerDuration, value);
        }
        private int? _firstLayerDuration;

        /// <summary>
        /// Total duration of the last job (in s or null)
        /// </summary>
        public int? LastDuration
        {
            get => _lastDuration;
			set => SetPropertyValue(ref _lastDuration, value);
        }
        private int? _lastDuration;

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
        [SbcProperty(false)]
        public bool LastFileAborted
        {
            get => _lastFileAborted;
			set => SetPropertyValue(ref _lastFileAborted, value);
        }
        private bool _lastFileAborted;
        
        /// <summary>
        /// Indicates if the last file was cancelled (user cancelled)
        /// </summary>
        [SbcProperty(false)]
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
        [SbcProperty(false)]
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
        /// Information about the past layers
        /// </summary>
        /// <remarks>
        /// In previous API versions this was a <see cref="ModelGrowingCollection{T}"/> but it has been changed to <see cref="ModelCollection{T}"/> to
        /// allow past layers to be modified again when needed. Note that previous plugins subscribing to this property will not receive any more
        /// updates about this property to avoid memory leaks
        /// </remarks>
        /// <seealso cref="Layer"/>
        [SbcProperty(false)]
        public ModelCollection<Layer> Layers { get; } = new ModelCollection<Layer>();

        /// <summary>
        /// Time elapsed since the last layer change (in s or null)
        /// </summary>
        public float? LayerTime
        {
            get => _layerTime;
			set => SetPropertyValue(ref _layerTime, value);
        }
        private float? _layerTime;
        
        /// <summary>
        /// Total pause time since the job started
        /// </summary>
        public int? PauseDuration
        {
            get => _pauseDuration;
            set => SetPropertyValue(ref _pauseDuration, value);
        }
        private int? _pauseDuration;

        /// <summary>
        /// Total extrusion amount without extrusion factors applied (in mm)
        /// </summary>
        public float? RawExtrusion
        {
            get => _rawExtrusion;
            set => SetPropertyValue(ref _rawExtrusion, value);
        }
        private float? _rawExtrusion;

        /// <summary>
        /// Estimated times left
        /// </summary>
        public TimesLeft TimesLeft { get; } = new TimesLeft();

        /// <summary>
        /// Time needed to heat up the heaters (in s or null)
        /// </summary>
        public int? WarmUpDuration
        {
            get => _warmUpDuration;
			set => SetPropertyValue(ref _warmUpDuration, value);
        }
        private int? _warmUpDuration;
    }
}
