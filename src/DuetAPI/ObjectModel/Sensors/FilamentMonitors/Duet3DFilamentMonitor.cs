namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Base class for Duet3D filament monitors
    /// </summary>
    public class Duet3DFilamentMonitor : FilamentMonitor
    {

        /// <summary>
        /// Average ratio of measured vs. commanded movement
        /// </summary>
        public int? AvgPercentage
        {
            get => _avgPercentage;
            set => SetPropertyValue(ref _avgPercentage, value);
        }
        private int? _avgPercentage;

        /// <summary>
        /// Last ratio of measured vs. commanded movement
        /// </summary>
        public int? LastPercentage
        {
            get => _lastPercentage;
            set => SetPropertyValue(ref _lastPercentage, value);
        }
        private int? _lastPercentage;

        /// <summary>
        /// Maximum ratio of measured vs. commanded movement
        /// </summary>
        public int? MaxPercentage
        {
            get => _maxPercentage;
            set => SetPropertyValue(ref _maxPercentage, value);
        }
        private int? _maxPercentage;

        /// <summary>
        /// Minimum ratio of measured vs. commanded movement
        /// </summary>
        public int? MinPercentage
        {
            get => _minPercentage;
            set => SetPropertyValue(ref _minPercentage, value);
        }
        private int? _minPercentage;

        /// <summary>
        /// Total extrusion commanded (in mm)
        /// </summary>
        public float TotalExtrusion
        {
            get => _totalExtrusion;
            set => SetPropertyValue(ref _totalExtrusion, value);
        }
        private float _totalExtrusion;
    }
}
