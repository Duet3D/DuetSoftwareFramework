namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Calibrated properties of a rotating magnet filament monitor
    /// </summary>
    public partial class RotatingMagnetFilamentMonitorCalibrated : ModelObject
    {
        /// <summary>
        /// Extruded distance per revolution (in mm)
        /// </summary>
        public float MmPerRev
        {
            get => _mmPerRev;
            set => SetPropertyValue(ref _mmPerRev, value);
        }
        private float _mmPerRev;

        /// <summary>
        /// Maximum percentage (0..1 or greater)
        /// </summary>
        public float PercentMax
        {
            get => _percentMax;
            set => SetPropertyValue(ref _percentMax, value);
        }
        private float _percentMax;

        /// <summary>
        /// Minimum percentage (0..1)
        /// </summary>
        public float PercentMin
        {
            get => _percentMin;
            set => SetPropertyValue(ref _percentMin, value);
        }
        private float _percentMin;

        /// <summary>
        /// Total extruded distance (in mm)
        /// </summary>
        public float TotalDistance
        {
            get => _totalDistance;
            set => SetPropertyValue(ref _totalDistance, value);
        }
        private float _totalDistance;
    }

    /// <summary>
    /// Configured properties of a rotating magnet filament monitor
    /// </summary>
    public class RotatingMagnetFilamentMonitorConfigured : ModelObject
    {
        /// <summary>
        /// Whether all moves and not only printing moves are supposed to be checked
        /// </summary>
        public bool AllMoves
        {
            get => _allMoves;
            set => SetPropertyValue(ref _allMoves, value);
        }
        private bool _allMoves;

        /// <summary>
        /// Extruded distance per revolution (in mm)
        /// </summary>
        public float MmPerRev
        {
            get => _mmPerRev;
            set => SetPropertyValue(ref _mmPerRev, value);
        }
        private float _mmPerRev;

        /// <summary>
        /// Maximum percentage (0..1 or greater)
        /// </summary>
        public float PercentMax
        {
            get => _percentMax;
            set => SetPropertyValue(ref _percentMax, value);
        }
        private float _percentMax;

        /// <summary>
        /// Minimum percentage (0..1)
        /// </summary>
        public float PercentMin
        {
            get => _percentMin;
            set => SetPropertyValue(ref _percentMin, value);
        }
        private float _percentMin;

        /// <summary>
        /// Sample distance (in mm)
        /// </summary>
        public float SampleDistance
        {
            get => _sampleDistance;
            set => SetPropertyValue(ref _sampleDistance, value);
        }
        private float _sampleDistance;
    }

    /// <summary>
    /// Information about a rotating magnet filament monitor
    /// </summary>
    public class RotatingMagnetFilamentMonitor : Duet3DFilamentMonitor
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        public RotatingMagnetFilamentMonitor()
        {
            Type = FilamentMonitorType.RotatingMagnet;
        }

        /// <summary>
        /// Calibrated properties of this filament monitor
        /// </summary>
        public RotatingMagnetFilamentMonitorCalibrated Calibrated
        {
            get => _calibrated;
            set => SetPropertyValue(ref _calibrated, value);
        }
        private RotatingMagnetFilamentMonitorCalibrated _calibrated = new();

        /// <summary>
        /// Configured properties of this filament monitor
        /// </summary>
        public RotatingMagnetFilamentMonitorConfigured Configured { get; } = new();
    }
}
