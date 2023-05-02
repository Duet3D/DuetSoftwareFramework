namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the SBC's memory (RAM)
    /// </summary>
    public class Memory : ModelObject
    {
        /// <summary>
        /// Available memory (in bytes)
        /// </summary>
        public long? Available
        {
            get => _available;
            set => SetPropertyValue(ref _available, value);
        }
        private long? _available;

        /// <summary>
        /// Total memory (in bytes)
        /// </summary>
        public long? Total
        {
            get => _total;
            set => SetPropertyValue(ref _total, value);
        }
        private long? _total;
    }
}
