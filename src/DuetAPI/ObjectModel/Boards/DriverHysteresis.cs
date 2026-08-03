namespace DuetAPI.ObjectModel;

/// <summary>
/// spreadCycle hysteresis settings of a driver, as set by the Y parameter of M569
/// </summary>
public partial class DriverHysteresis : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Hysteresis start value
    /// </summary>
    public int Start
    {
        get => _start;
        set => SetPropertyValue(ref _start, value);
    }
    private int _start = 5;

    /// <summary>
    /// Hysteresis end value
    /// </summary>
    public int End
    {
        get => _end;
        set => SetPropertyValue(ref _end, value);
    }
    private int _end = 0;

    /// <summary>
    /// Hysteresis decrement value
    /// </summary>
    public int Decrement
    {
        get => _decrement;
        set => SetPropertyValue(ref _decrement, value);
    }
    private int _decrement;
}
