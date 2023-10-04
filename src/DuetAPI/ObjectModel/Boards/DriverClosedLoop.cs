namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the current fraction of the closed-loop configuration
    /// </summary>
    public sealed class ClosedLoopCurrentFraction : ModelObject
    {
        /// <summary>
        /// Average fraction
        /// </summary>
        public float Avg
        {
            get => _avg;
            set => SetPropertyValue(ref _avg, value);
        }
        private float _avg;

        /// <summary>
        /// Maximum fraction
        /// </summary>
        public float Max
        {
            get => _max;
            set => SetPropertyValue(ref _max, value);
        }
        private float _max;
    }

    /// <summary>
    /// Information about the current fraction of the closed-loop configuration
    /// </summary>
    public sealed class ClosedLoopPositionError : ModelObject
    {
        /// <summary>
        /// Maximum position error
        /// </summary>
        public float Max
        {
            get => _max;
            set => SetPropertyValue(ref _max, value);
        }
        private float _max;

        /// <summary>
        /// RMS of the position error
        /// </summary>
        public float Rms
        {
            get => _rms;
            set => SetPropertyValue(ref _rms, value);
        }
        private float _rms;
    }

    /// <summary>
    /// This represents information about closed-loop tuning
    /// </summary>
    public sealed class DriverClosedLoop : ModelObject
    {
        /// <summary>
        /// Current fraction
        /// </summary>
        public ClosedLoopCurrentFraction CurrentFraction { get; } = new ClosedLoopCurrentFraction();

        /// <summary>
        /// Position error
        /// </summary>
        public ClosedLoopPositionError PositionError { get; } = new ClosedLoopPositionError();
    }
}
