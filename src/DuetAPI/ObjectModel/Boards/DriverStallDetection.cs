namespace DuetAPI.ObjectModel;

/// <summary>
/// Configured (M915) stall detection settings of a driver
/// </summary>
public partial class DriverStallDetection : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Stall detection threshold
    /// </summary>
    public int Threshold
    {
        get => _threshold;
        set => SetPropertyValue(ref _threshold, value);
    }
    private int _threshold = 1;

    /// <summary>
    /// Whether the stall detection filter is enabled
    /// </summary>
    public bool Filter
    {
        get => _filter;
        set => SetPropertyValue(ref _filter, value);
    }
    private bool _filter;

    /// <summary>
    /// Minimum speed at which stall detection is enabled (in steps/s)
    /// </summary>
    public int MinimumSpeed
    {
        get => _minimumSpeed;
        set => SetPropertyValue(ref _minimumSpeed, value);
    }
    private int _minimumSpeed = 200;

    /// <summary>
    /// coolStep register value
    /// </summary>
    public int CoolStep
    {
        get => _coolStep;
        set => SetPropertyValue(ref _coolStep, value);
    }
    private int _coolStep;

    /// <summary>
    /// Whether an event is raised when this driver stalls
    /// </summary>
    public bool RaiseEvent
    {
        get => _raiseEvent;
        set => SetPropertyValue(ref _raiseEvent, value);
    }
    private bool _raiseEvent;
}
