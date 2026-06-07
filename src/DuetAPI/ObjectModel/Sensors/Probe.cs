using System;
using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a configured probe
/// </summary>
public partial class Probe : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Calibration temperature (in C)
    /// </summary>
    public float CalibrationTemperature
    {
        get => _calibrationTemperature;
        set => SetPropertyValue(ref _calibrationTemperature, value);
    }
    private float _calibrationTemperature;

    /// <summary>
    /// Indicates if the user has deployed the probe
    /// </summary>
    public bool DeployedByUser
    {
        get => _deployedByUser;
        set => SetPropertyValue(ref _deployedByUser, value);
    }
    private bool _deployedByUser;

    /// <summary>
    /// Whether probing disables the heater(s)
    /// </summary>
    public bool DisablesHeaters
    {
        get => _disablesHeaters;
        set => SetPropertyValue(ref _disablesHeaters, value);
    }
    private bool _disablesHeaters;

    /// <summary>
    /// Dive height of the probe (in mm)
    /// </summary>
    [Obsolete("Use DiveHeights instead")]
    public float DiveHeight
    {
        get => _diveHeight;
        set => SetPropertyValue(ref _diveHeight, value);
    }
    private float _diveHeight;

    /// <summary>
    /// Dive heights of the probe.
    /// The first element is the regular dive height, the second element may be used by scanning Z-probes
    /// </summary>
    public ObservableCollection<float> DiveHeights { get; } = [0F, 0F];

    /// <summary>
    /// Indicates if the scanning probe is calibrated
    /// </summary>
    public bool? IsCalibrated
    {
        get => _isCalibrated;
        set => SetPropertyValue(ref _isCalibrated, value);
    }
    private bool? _isCalibrated;

    /// <summary>
    /// Height of the probe where it stopped last time (in mm)
    /// </summary>
    public float LastStopHeight
    {
        get => _lastStopHeight;
        set => SetPropertyValue(ref _lastStopHeight, value);
    }
    private float _lastStopHeight;

    /// <summary>
    /// Maximum number of times to probe after a bad reading was determined
    /// </summary>
    public int MaxProbeCount
    {
        get => _maxProbeCount;
        set => SetPropertyValue(ref _maxProbeCount, value);
    }
    private int _maxProbeCount = 1;

    /// <summary>
    /// Measured height (only applicable for scanning probes, in mm or null)
    /// </summary>
    [Live]
    public float? MeasuredHeight
    {
        get => _measuredHeight;
        set => SetPropertyValue(ref _measuredHeight, value);
    }
    private float? _measuredHeight;

    /// <summary>
    /// X+Y offsets (in mm)
    /// </summary>
    public ObservableCollection<float> Offsets { get; } = [0F, 0F];

    /// <summary>
    /// Recovery time (in s)
    /// </summary>
    public float RecoveryTime
    {
        get => _recoveryTime;
        set => SetPropertyValue(ref _recoveryTime, value);
    }
    private float _recoveryTime;

    /// <summary>
    /// Coefficients for the scanning Z-probe (4 elements, if applicable)
    /// </summary>
    public ObservableCollection<float>? ScanCoefficients
    {
        get => _scanCoefficients;
        set => SetPropertyValue(ref _scanCoefficients, value);
    }
    private ObservableCollection<float>? _scanCoefficients;

    /// <summary>
    /// Fast and slow probing speeds (in mm/s).
    /// Scanning probes may have three speeds where the last one is the movement speed while probing heightmaps
    /// </summary>
    public ObservableCollection<float> Speeds { get; } = [2F, 2F];

    /// <summary>
    /// List of temperature coefficients
    /// </summary>
    public ObservableCollection<float> TemperatureCoefficients { get; } = [0F, 0F];

    /// <summary>
    /// Configured trigger threshold (0..1023)
    /// </summary>
    public int Threshold
    {
        get => _threshold;
        set => SetPropertyValue(ref _threshold, value);
    }
    private int _threshold = 500;

    /// <summary>
    /// Allowed tolerance deviation between two measures (in mm)
    /// </summary>
    public float Tolerance
    {
        get => _tolerance;
        set => SetPropertyValue(ref _tolerance, value);
    }
    private float _tolerance = 0.03F;

    /// <summary>
    /// Touch mode options (if supported, otherwise null)
    /// </summary>
    public ProbeTouchMode? TouchMode
    {
        get => _touchMode;
        set => SetPropertyValue(ref _touchMode, value);
    }
    private ProbeTouchMode? _touchMode;

    /// <summary>
    /// Travel speed when probing multiple points (in mm/min)
    /// </summary>
    public float TravelSpeed
    {
        get => _travelSpeed;
        set => SetPropertyValue(ref _travelSpeed, value);
    }
    private float _travelSpeed = 6000F;

    /// <summary>
    /// Z height at which the probe is triggered (in mm)
    /// </summary>
    public float TriggerHeight
    {
        get => _triggerHeight;
        set => SetPropertyValue(ref _triggerHeight, value);
    }
    private float _triggerHeight = 0.7F;

    /// <summary>
    /// Type of the configured probe
    /// </summary>
    /// <seealso cref="ProbeType"/>
    public ProbeType Type
    {
        get => _type;
        set => SetPropertyValue(ref _type, value);
    }
    private ProbeType _type = ProbeType.None;

    /// <summary>
    /// Current analog values of the probe
    /// </summary>
    [Live]
    public ObservableCollection<int> Value { get; } = [];
}
