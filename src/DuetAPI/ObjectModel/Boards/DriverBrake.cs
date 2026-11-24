namespace DuetAPI.ObjectModel;

/// <summary>
/// Configured (M569.7) brake of a driver
/// </summary>
public partial class DriverBrake : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Delay in milliseconds between engaging the brake and disabling the driver, or null for the board's default (M569.7 S)
    /// </summary>
    /// <remarks>
    /// Assigning a port resets the delay to the board's default, so this is null again after a C parameter without S
    /// </remarks>
    public int? Delay
    {
        get => _delay;
        set => SetPropertyValue(ref _delay, value);
    }
    private int? _delay;

    /// <summary>
    /// Port controlling the brake as given to M569.7 C, or null if the driver has no brake
    /// </summary>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// Voltage the brake is limited to by PWM, or null if it has not been limited (M569.7 V)
    /// </summary>
    public float? Voltage
    {
        get => _voltage;
        set => SetPropertyValue(ref _voltage, value);
    }
    private float? _voltage;
}
