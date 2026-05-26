namespace DuetAPI.ObjectModel;

/// <summary>
/// Configured (M569) settings of a driver
/// </summary>
public partial class DriverConfig : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Configured direction of the driver (0 = reverse, 1 = forward)
    /// </summary>
    public int Direction
    {
        get => _direction;
        set => SetPropertyValue(ref _direction, value);
    }
    private int _direction;

    /// <summary>
    /// Configured driver mode (only available for smart drivers)
    /// </summary>
    public DriverMode? Mode
    {
        get => _mode;
        set => SetPropertyValue(ref _mode, value);
    }
    private DriverMode? _mode;
}
