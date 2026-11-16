namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a configured LED strip
/// </summary>
public partial class LedStrip : ModelObject, IStaticModelObject
{
    /// <summary>Most LEDs a strip created by M950 E without U can address</summary>
    /// <remarks>RepRapFirmware's <c>DefaultMaxLedsPerStrip</c></remarks>
    public const uint DefaultMaxLeds = 60;

    /// <summary>
    /// Board address of the corresponding pin
    /// </summary>
    public int Board
    {
        get => _board;
        set => SetPropertyValue(ref _board, value);
    }
    private int _board;

    /// <summary>
    /// Order in which colour components are sent to the strip
    /// </summary>
    public LedStripColorOrder ColorOrder
    {
        get => _colorOrder;
        set => SetPropertyValue(ref _colorOrder, value);
    }
    private LedStripColorOrder _colorOrder = LedStripColorOrder.BGR;

    /// <summary>
    /// Clock frequency the strip is driven at as given to M950 E Q (in Hz), or null for the board's default for the type
    /// </summary>
    /// <remarks>Sent to the board that drives the strip, so it is kept to recreate the strip after a restart</remarks>
    public uint? Frequency
    {
        get => _frequency;
        set => SetPropertyValue(ref _frequency, value);
    }
    private uint? _frequency;

    /// <summary>
    /// Maximum number of LEDs that can be addressed on this strip
    /// </summary>
    public uint MaxLeds
    {
        get => _maxLeds;
        set => SetPropertyValue(ref _maxLeds, value);
    }
    private uint _maxLeds;

    /// <summary>
    /// Name of the pin this LED strip is connected to
    /// </summary>
    public string Pin
    {
        get => _pin;
        set => SetPropertyValue(ref _pin, value);
    }
    private string _pin = string.Empty;

    /// <summary>
    /// Indicates if this strip is bit-banged and therefore requires motion to be stopped before sending a command
    /// </summary>
    public bool StopMovement
    {
        get => _stopMovement;
        set => SetPropertyValue(ref _stopMovement, value);
    }
    private bool _stopMovement;

    /// <summary>
    /// Type of this LED strip
    /// </summary>
    public LedStripType Type
    {
        get => _type;
        set => SetPropertyValue(ref _type, value);
    }
    private LedStripType _type = LedStripType.DotStar;
}
