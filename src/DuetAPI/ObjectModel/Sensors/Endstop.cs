namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about an endstop
/// </summary>
public partial class Endstop : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Whether this endstop is at the high end of the axis
    /// </summary>
    public bool HighEnd
    {
        get => _highEnd;
        set => SetPropertyValue(ref _highEnd, value);
    }
    private bool _highEnd;

    /// <summary>
    /// Number of the referenced probe if type is ZProbeAsEndstop, else null
    /// </summary>
    public int? Probe
    {
        get => _probe;
        set => SetPropertyValue(ref _probe, value);
    }
    private int? _probe;

    /// <summary>
    /// Port of this endstop as given to M574, or null if it has none
    /// </summary>
    /// <remarks>
    /// The expansion board carrying the port is what watches the input, but the port is recorded
    /// here because the object model has to hold enough to recreate the machine: a board that
    /// reconnects is given its input monitor again from this, and M500 writes it back out
    /// </remarks>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// Whether or not the endstop is hit
    /// </summary>
    [Live]
    public bool Triggered
    {
        get => _triggered;
        set => SetPropertyValue(ref _triggered, value);
    }
    private bool _triggered;
    
    /// <summary>
    /// Type of the endstop
    /// </summary>
    public EndstopType Type
    {
        get => _type;
        set => SetPropertyValue(ref _type, value);
    }
    private EndstopType _type = EndstopType.Unknown;
}
