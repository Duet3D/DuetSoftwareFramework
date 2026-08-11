namespace DuetAPI.ObjectModel;

/// <summary>
/// Details about a general-purpose output port
/// </summary>
public partial class GpOutputPort : ModelObject, IStaticModelObject
{
    /// <summary>
    /// PWM frequency of this port (in Hz)
    /// </summary>
    public int Freq
    {
        get => _freq;
        set => SetPropertyValue(ref _freq, value);
    }
    private int _freq;

    /// <summary>
    /// Port as given to M950, or null if it has none
    /// </summary>
    /// <remarks>
    /// The expansion board carrying the port is what drives it, but the port is recorded here
    /// because the object model has to hold enough to recreate the machine: without it M42 has no
    /// way to know which board to address after a restart
    /// </remarks>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// PWM value of this port (0..1)
    /// </summary>
    public float Pwm
    {
        get => _pwm;
        set => SetPropertyValue(ref _pwm, value);
    }
    private float _pwm;
}
