namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the current fraction of the closed-loop configuration
    /// </summary>
    public partial class ClosedLoopCurrentFraction : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Average fraction of the configured motor current used
        /// </summary>
        public float Avg
        {
            get => _avg;
            set => SetPropertyValue(ref _avg, value);
        }
        private float _avg;

        /// <summary>
        /// Maximum fraction of the configured motor current used
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
    public partial class ClosedLoopPositionError : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Maximum position error in full steps of the motor
        /// </summary>
        public float Max
        {
            get => _max;
            set => SetPropertyValue(ref _max, value);
        }
        private float _max;

        /// <summary>
        /// RMS of the position error in full steps of the motor
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
    public partial class DriverClosedLoop : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Current fraction f the configured motor current used
        /// </summary>
        public ClosedLoopCurrentFraction CurrentFraction { get; } = new ClosedLoopCurrentFraction();

        /// <summary>
        /// Position error in full steps of the motor
        /// </summary>
        public ClosedLoopPositionError PositionError { get; } = new ClosedLoopPositionError();
    }
}
