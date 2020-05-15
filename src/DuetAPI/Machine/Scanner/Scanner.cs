namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the 3D scanner subsystem
    /// </summary>
    public sealed class Scanner : ModelObject
    {
        /// <summary>
        /// Progress of the current action (on a scale between 0 to 1)
        /// </summary>
        /// <remarks>
        /// Previous status responses used a scale of 0..100
        /// </remarks>
        public float Progress
        {
            get => _progress;
			set => SetPropertyValue(ref _progress, value);
        }
        private float _progress;
        
        /// <summary>
        /// Status of the 3D scanner
        /// </summary>
        /// <seealso cref="ScannerStatus"/>
        public ScannerStatus Status
        {
            get => _status;
			set => SetPropertyValue(ref _status, value);
        }
        private ScannerStatus _status = ScannerStatus.Disconnected;
    }
}