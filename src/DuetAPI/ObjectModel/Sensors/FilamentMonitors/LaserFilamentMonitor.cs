namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Calibrated properties of a laser filament monitor
    /// </summary>
    public sealed class LaserFilamentMonitorCalibrated : ModelObject
    {
        /// <summary>
        /// Calibration factor of this sensor
        /// </summary>
        public float CalibrationFactor
        {
            get => _calibrationFactor;
            set => SetPropertyValue(ref _calibrationFactor, value);
        }
        private float _calibrationFactor;

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
        /// Calibrated sensivity
        /// </summary>
        public float Sensivity
        {
            get => _sensivity;
            set => SetPropertyValue(ref _sensivity, value);
        }
        private float _sensivity;

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
    /// Configured properties of a laser filament monitor
    /// </summary>
    public class LaserFilamentMonitorConfigured : ModelObject
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
	/// Information about a laser filament monitor
	/// </summary>
    public class LaserFilamentMonitor : FilamentMonitor
    {
		/// <summary>
		/// Constructor of this class
		/// </summary>
		public LaserFilamentMonitor()
		{
			Type = FilamentMonitorType.Laser;
		}

		/// <summary>
		/// Calibrated properties of this filament monitor
		/// </summary>
		public LaserFilamentMonitorCalibrated Calibrated
		{
			get => _calibrated;
			set => SetPropertyValue(ref _calibrated, value);
		}
		private LaserFilamentMonitorCalibrated _calibrated = new();

		/// <summary>
		/// Configured properties of this filament monitor
		/// </summary>
		public LaserFilamentMonitorConfigured Configured { get; } = new LaserFilamentMonitorConfigured();

		/// <summary>
		/// Indicates if a filament is present
		/// </summary>
		public bool? FilamentPresent
		{
			get => _filamentPresent;
			set => SetPropertyValue(ref _filamentPresent, value);
		}
		private bool? _filamentPresent;
	}
}
