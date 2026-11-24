namespace DuetAPI.ObjectModel;

/// <summary>
/// Details about a general-purpose input port
/// </summary>
public partial class GpInputPort : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Port as given to M950 J, or null if it has none
    /// </summary>
    /// <remarks>
    /// The expansion board carrying the port is what watches it, and the port is recorded here so
    /// that the board can be asked to watch it again after a restart
    /// </remarks>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// Value of this port (0..1)
    /// </summary>
    public float Value
    {
        get => _value;
        set => SetPropertyValue(ref _value, value);
    }
    private float _value;
}
