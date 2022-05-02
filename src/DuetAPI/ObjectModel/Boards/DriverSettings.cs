namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about driver settings
    /// </summary>
    public sealed class DriverSettings : ModelObject
    {
        /// <summary>
        /// Whether the drive goes forwards
        /// </summary>
        public bool Forwards
        {
            get => _forwards;
            set => SetPropertyValue(ref _forwards, value);
        }
        private bool _forwards = true;

        /// <summary>
        /// Stall detection settings
        /// </summary>
        public StallDetectSettings StallDetect { get; } = new StallDetectSettings();
    }
}
