namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Calibrated properties of a pulsed filament monitor
    /// </summary>
    public partial class PulsedFilamentMonitorCalibrated : ModelObject, IStaticModelObject
	{
		/// <summary>
		/// Extruded distance per pulse (in mm)
		/// </summary>
		public float MmPerPulse
		{
			get => _mmPerPulse;
			set => SetPropertyValue(ref _mmPerPulse, value);
		}
		private float _mmPerPulse;

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
	/// Configured properties of a pulsed filament monitor
	/// </summary>
	public partial class PulsedFilamentMonitorConfigured : ModelObject, IStaticModelObject
	{
		/// <summary>
		/// Extruded distance per pulse (in mm)
		/// </summary>
		public float MmPerPulse
		{
			get => _mmPerPulse;
			set => SetPropertyValue(ref _mmPerPulse, value);
		}
		private float _mmPerPulse;

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
	/// Information about a pulsed filament monitor
	/// </summary>
    public partial class PulsedFilamentMonitor : FilamentMonitor
    {
		/// <summary>
		/// Constructor of this class
		/// </summary>
		public PulsedFilamentMonitor()
		{
			Type = FilamentMonitorType.Pulsed;
		}

		/// <summary>
		/// Calibrated properties of this filament monitor
		/// </summary>
		public PulsedFilamentMonitorCalibrated Calibrated
		{
			get => _calibrated;
			set => SetPropertyValue(ref _calibrated, value);
		}
		private PulsedFilamentMonitorCalibrated _calibrated = new();

		/// <summary>
		/// Configured properties of this filament monitor
		/// </summary>
		public PulsedFilamentMonitorConfigured Configured { get; } = new PulsedFilamentMonitorConfigured();
    }
}
