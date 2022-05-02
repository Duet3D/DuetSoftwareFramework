namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about stall detection
    /// </summary>
    public sealed class StallDetectSettings : ModelObject
    {
        /// <summary>
        /// Stall detection threshold
        /// </summary>
        public int Threshold
        {
            get => _threshold;
            set => SetPropertyValue(ref _threshold, value);
        }
        private int _threshold;

        /// <summary>
        /// Whether the input values are filtered
        /// </summary>
        public bool Filtered
        {
            get => _filtered;
            set => SetPropertyValue(ref _filtered, value);
        }
        private bool _filtered;

        /// <summary>
        /// Minimum steps
        /// </summary>
        public int MinSteps
        {
            get => _minSteps;
            set => SetPropertyValue(ref _minSteps, value);
        }
        private int _minSteps;

        /// <summary>
        /// Coolstep register value
        /// </summary>
        public long Coolstep
        {
            get => _coolstep;
            set => SetPropertyValue(ref _coolstep, value);
        }
        private long _coolstep;


        /// <summary>
        /// Action to perform when a stall is detected 
        /// </summary>
        public int Action
        {
            get => _action;
            set => SetPropertyValue(ref _action, value);
        }
        private int _action;
    }
}
