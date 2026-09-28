namespace DuetAPI.ObjectModel;

/// <summary>
/// Base class for Duet3D filament monitors
/// </summary>
public partial class Duet3DFilamentMonitor : FilamentMonitor
{

    /// <summary>
    /// Average ratio of measured vs. commanded movement
    /// </summary>
    [Live]
    public int? AvgPercentage
    {
        get => _avgPercentage;
        set => SetPropertyValue(ref _avgPercentage, value);
    }
    private int? _avgPercentage;

    /// <summary>
    /// Last ratio of measured vs. commanded movement
    /// </summary>
    [Live]
    public int? LastPercentage
    {
        get => _lastPercentage;
        set => SetPropertyValue(ref _lastPercentage, value);
    }
    private int? _lastPercentage;

    /// <summary>
    /// Maximum ratio of measured vs. commanded movement
    /// </summary>
    [Live]
    public int? MaxPercentage
    {
        get => _maxPercentage;
        set => SetPropertyValue(ref _maxPercentage, value);
    }
    private int? _maxPercentage;

    /// <summary>
    /// Minimum ratio of measured vs. commanded movement
    /// </summary>
    [Live]
    public int? MinPercentage
    {
        get => _minPercentage;
        set => SetPropertyValue(ref _minPercentage, value);
    }
    private int? _minPercentage;

        /// <summary>
        /// Reported sensor position of this filament monitor.
        /// The maximum value depends on the type of the sensor, e.g. 0..1023 for a Duet3D MFM.
        /// </summary>
        [Live]
        public int Position
        {
            get => _position;
            set => SetPropertyValue(ref _position, value);
        }
        private int _position;

    /// <summary>
    /// Total extrusion commanded (in mm)
    /// </summary>
    [Live]
    public float TotalExtrusion
    {
        get => _totalExtrusion;
        set => SetPropertyValue(ref _totalExtrusion, value);
    }
    private float _totalExtrusion;
}
