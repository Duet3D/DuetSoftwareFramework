namespace DuetAPI.ObjectModel;

/// <summary>
/// Class representing information about an attached fan
/// </summary>
public partial class Fan : ModelObject, IStaticModelObject
{
    /// <summary>Temperature a thermostatic fan triggers at until M106 T says otherwise (in C)</summary>
    /// <remarks>RepRapFirmware's <c>DefaultHotEndFanTemperature</c></remarks>
    public const float DefaultTriggerTemperature = 45F;

    /// <summary>Fewest tacho pulses per revolution a fan may be configured with</summary>
    /// <remarks>
    /// CANlib's <c>MinFanPulsesPerRev</c>. Below about 0.2 the reciprocal a board divides by to reach
    /// an RPM overflows, so a smaller figure would not give a wrong speed but no speed at all
    /// </remarks>
    public const float MinTachoPpr = 0.5F;

    /// <summary>Most tacho pulses per revolution a fan may be configured with</summary>
    /// <remarks>CANlib's <c>MaxFanPulsesPerRev</c></remarks>
    public const float MaxTachoPpr = 200F;

    /// <summary>
    /// Value of this fan (0..1 or -1 if unknown)
    /// </summary>
    /// <remarks>
    /// The expansion board carrying the fan is what measures this, so it stays unknown until the
    /// board has reported a PWM
    /// </remarks>
    [Live]
    public float ActualValue
    {
        get => _actualValue;
        set => SetPropertyValue(ref _actualValue, value);
    }
    private float _actualValue = -1F;

    /// <summary>
    /// Blip value indicating how long the fan is supposed to run at 100% when turning it on to get it started (in s)
    /// </summary>
    public float Blip
    {
        get => _blip;
        set => SetPropertyValue(ref _blip, value);
    }
    private float _blip = 0.1F;

    /// <summary>
    /// Configured frequency of this fan (in Hz)
    /// </summary>
    public float Frequency
    {
        get => _frequency;
        set => SetPropertyValue(ref _frequency, value);
    }
    private float _frequency = 250;

    /// <summary>
    /// Maximum value of this fan (0..1)
    /// </summary>
    public float Max
    {
        get => _max;
        set => SetPropertyValue(ref _max, value);
    }
    private float _max = 1F;

    /// <summary>
    /// Minimum value of this fan (0..1)
    /// </summary>
    /// <remarks>
    /// The floor a fan is driven at when it is on at all, because most fans stall below it.
    /// RepRapFirmware's <c>DefaultMinFanPwm</c>
    /// </remarks>
    public float Min
    {
        get => _min;
        set => SetPropertyValue(ref _min, value);
    }
    private float _min = 0.1F;

    /// <summary>
    /// Name of the fan
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetPropertyValue(ref _name, value);
    }
    private string _name = string.Empty;

    /// <summary>
    /// Requested value for this fan on a scale between 0 to 1
    /// </summary>
    [Live]
    public float RequestedValue
    {
        get => _requestedValue;
        set => SetPropertyValue(ref _requestedValue, value);
    }
    private float _requestedValue;
    
    /// <summary>
    /// Port of this fan as given to M950, or null if it has none
    /// </summary>
    /// <remarks>
    /// The expansion board carrying the port is what drives the fan, but the port is recorded here
    /// because the object model has to hold enough to recreate the machine: without it a fan cannot
    /// be addressed after a restart, because nothing says which board it is on
    /// </remarks>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// Current RPM of this fan or -1 if unknown/unset
    /// </summary>
    [Live]
    public int Rpm
    {
        get => _rpm;
        set => SetPropertyValue(ref _rpm, value);
    }
    private int _rpm = -1;

    /// <summary>
    /// Pulses per tacho revolution
    /// </summary>
    public float TachoPpr
    {
        get => _tachoPpr;
        set => SetPropertyValue(ref _tachoPpr, value);
    }
    private float _tachoPpr = 2.0F;
    
    /// <summary>
    /// Thermostatic control parameters
    /// </summary>
    public FanThermostaticControl Thermostatic { get; } = new FanThermostaticControl();
}
