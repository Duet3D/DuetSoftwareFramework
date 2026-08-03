using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Configured (M569) settings of a driver
/// </summary>
/// <remarks>
/// The driver itself lives on an expansion board and is what acts on these, but they are kept here
/// as well because the object model has to hold enough to recreate the machine: a board that
/// reconnects is reconfigured from this, and M500 writes config-override.g from it
/// </remarks>
public partial class DriverConfig : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Blanking time of the driver (M569 B)
    /// </summary>
    public int? BlankingTime
    {
        get => _blankingTime;
        set => SetPropertyValue(ref _blankingTime, value);
    }
    private int? _blankingTime;

    /// <summary>
    /// coolStep threshold, as a microstep interval (M569 H)
    /// </summary>
    public int? CoolStepThreshold
    {
        get => _coolStepThreshold;
        set => SetPropertyValue(ref _coolStepThreshold, value);
    }
    private int? _coolStepThreshold;

    /// <summary>
    /// Current scaler of the driver (M569 U)
    /// </summary>
    public int? CurrentScaler
    {
        get => _currentScaler;
        set => SetPropertyValue(ref _currentScaler, value);
    }
    private int? _currentScaler;

    /// <summary>
    /// Configured direction of the driver (false = reverse, true = forward)
    /// </summary>
    public bool Direction
    {
        get => _direction;
        set => SetPropertyValue(ref _direction, value);
    }
    private bool _direction = true;

    /// <summary>
    /// Value the enable pin takes to enable the driver, or null if it has not been set (M569 R)
    /// </summary>
    public int? EnablePolarity
    {
        get => _enablePolarity;
        set => SetPropertyValue(ref _enablePolarity, value);
    }
    private int? _enablePolarity;

    /// <summary>
    /// spreadCycle hysteresis settings (M569 Y)
    /// </summary>
    public DriverHysteresis Hysteresis { get; } = new DriverHysteresis();

    /// <summary>
    /// Configured driver mode (only available for smart drivers)
    /// </summary>
    public DriverMode? Mode
    {
        get => _mode;
        set => SetPropertyValue(ref _mode, value);
    }
    private DriverMode? _mode;

    /// <summary>
    /// Off time of the driver (M569 F)
    /// </summary>
    public int? OffTime
    {
        get => _offTime;
        set => SetPropertyValue(ref _offTime, value);
    }
    private int? _offTime;

    /// <summary>
    /// Stall detection settings of this driver
    /// </summary>
    public DriverStallDetection StallDetection { get; } = new DriverStallDetection();

    /// <summary>
    /// Microstep interval at which the driver changes from stealthChop to spreadCycle (M569 V)
    /// </summary>
    public int? StealthChopThreshold
    {
        get => _stealthChopThreshold;
        set => SetPropertyValue(ref _stealthChopThreshold, value);
    }
    private int? _stealthChopThreshold;

    /// <summary>
    /// Step pulse timings in microseconds, as step time, step interval, direction setup and direction hold (M569 T)
    /// </summary>
    public ObservableCollection<float> StepTiming { get; } = [];
}
