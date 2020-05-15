namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a storage device
    /// </summary>
    public sealed class Volume : ModelObject
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
        private string _name;

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
        /// Logical path of the storage device
        /// </summary>
        public string Path
        {
            get => _path;
			set => SetPropertyValue(ref _path, value);
        }
        private string _path;

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