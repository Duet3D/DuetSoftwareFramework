namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a storage device
    /// </summary>
    public partial class Volume : ModelObject
    {
        /// <summary>
        /// Total capacity of the storage device (in bytes or null)
        /// </summary>
        public long? Capacity
        {
            get => _capacity;
			set => SetPropertyValue(ref _capacity, value);
        }
        private long? _capacity;

        /// <summary>
        /// How much space is still available on this device (in bytes or null)
        /// </summary>
        public long? FreeSpace
        {
            get => _freeSpace;
			set => SetPropertyValue(ref _freeSpace, value);
        }
        private long? _freeSpace;

        /// <summary>
        /// Whether the storage device is mounted
        /// </summary>
        public bool Mounted
        {
            get => _mounted;
			set => SetPropertyValue(ref _mounted, value);
        }
        private bool _mounted;

        /// <summary>
        /// Name of this volume
        /// </summary>
        public string Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string _name = string.Empty;

        /// <summary>
        /// Number of currently open files or null if unknown
        /// </summary>
        public int? OpenFiles
        {
            get => _openFiles;
			set => SetPropertyValue(ref _openFiles, value);
        }
        private int? _openFiles;

        /// <summary>
        /// Total size of this volume (in bytes or null)
        /// </summary>

        public long? PartitionSize
        {
            get => _partitionSize;
            set => SetPropertyValue(ref _partitionSize, value);
        }
        private long? _partitionSize;

        /// <summary>
        /// Logical path of the storage device
        /// </summary>
        public string Path
        {
            get => _path;
			set => SetPropertyValue(ref _path, value);
        }
        private string _path = string.Empty;

        /// <summary>
        /// Speed of the storage device (in bytes/s or null if unknown)
        /// </summary>
        public int? Speed
        {
            get => _speed;
			set => SetPropertyValue(ref _speed, value);
        }
        private int? _speed;
    }
}