using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Holds information about a parsed G-code file
    /// </summary>
    public sealed class ParsedFileInfo : ModelObject
    {
        /// <summary>
        /// Filament consumption per extruder drive (in mm)
        /// </summary>
        public ModelCollection<float> Filament { get; } = new ModelCollection<float>();

        /// <summary>
        /// The filename of the G-code file
        /// </summary>
        public string FileName
        {
            get => _fileName;
			set => SetPropertyValue(ref _fileName, value);
        }
        private string _fileName;

        /// <summary>
        /// Height of the first layer or 0 if not found (in mm)
        /// </summary>
        public float FirstLayerHeight
        {
            get => _firstLayerHeight;
			set => SetPropertyValue(ref _firstLayerHeight, value);
        }
        private float _firstLayerHeight;

        /// <summary>
        /// Name of the application that generated this file
        /// </summary>
        public string GeneratedBy
        {
            get => _generatedBy;
			set => SetPropertyValue(ref _generatedBy, value);
        }
        private string _generatedBy;

        /// <summary>
        /// Build height of the G-code job or 0 if not found (in mm)
        /// </summary>
        public float Height
        {
            get => _height;
			set => SetPropertyValue(ref _height, value);
        }
        private float _height;

        /// <summary>
        /// Value indicating when the file was last modified or null if unknown
        /// </summary>
        public DateTime? LastModified
        {
            get => _lastModified;
            set => SetPropertyValue(ref _lastModified, value);
        }
        private DateTime? _lastModified;

        /// <summary>
        /// Height of each other layer or 0 if not found (in mm)
        /// </summary>
        public float LayerHeight
        {
            get => _layerHeight;
			set => SetPropertyValue(ref _layerHeight, value);
        }
        private float _layerHeight;

        /// <summary>
        /// Number of total layers or 0 if unknown
        /// </summary>
        public int NumLayers
        {
            get => _numLayers;
			set => SetPropertyValue(ref _numLayers, value);
        }
        private int _numLayers;

        /// <summary>
        /// Estimated print time (in s)
        /// </summary>
        public long? PrintTime
        {
            get => _printTime;
			set => SetPropertyValue(ref _printTime, value);
        }
        private long? _printTime;
        
        /// <summary>
        /// Estimated print time from G-code simulation (in s)
        /// </summary>
        public long? SimulatedTime
        {
            get => _simulatedTime;
			set => SetPropertyValue(ref _simulatedTime, value);
        }
        private long? _simulatedTime;

        /// <summary>
        /// Size of the file
        /// </summary>
        public long Size
        {
            get => _size;
			set => SetPropertyValue(ref _size, value);
        }
        private long _size;
    }
}