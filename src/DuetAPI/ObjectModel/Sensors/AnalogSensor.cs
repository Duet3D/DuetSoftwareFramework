namespace DuetAPI.ObjectModel;

/// <summary>
/// Representation of an analog sensor
/// </summary>
public partial class AnalogSensor : ModelObject, IStaticModelObject
{
    /// <summary>
    /// ADC high-end offset correction of a thermistor or PT1000 sensor, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 H
    /// </remarks>
    public int? AdcHighOffset
    {
        get => _adcHighOffset;
        set => SetPropertyValue(ref _adcHighOffset, value);
    }
    private int? _adcHighOffset;

    /// <summary>
    /// ADC low-end offset correction of a thermistor or PT1000 sensor, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 L
    /// </remarks>
    public int? AdcLowOffset
    {
        get => _adcLowOffset;
        set => SetPropertyValue(ref _adcLowOffset, value);
    }
    private int? _adcLowOffset;

    /// <summary>
    /// Share of a thermopile sensor's auxiliary output mixed into its reading, in 256ths, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 W
    /// </remarks>
    public int? AuxMix
    {
        get => _auxMix;
        set => SetPropertyValue(ref _auxMix, value);
    }
    private int? _auxMix;

    /// <summary>
    /// Beta value of a thermistor, or of a thermopile sensor's environment thermistor (if applicable)
    /// </summary>
    public float? Beta
    {
        get => _beta;
        set => SetPropertyValue(ref _beta, value);
    }
    private float? _beta;

    /// <summary>
    /// Steinhart-Hart C value of a thermistor, or of a thermopile sensor's environment thermistor
    /// </summary>
    public float? C
    {
        get => _c;
        set => SetPropertyValue(ref _c, value);
    }
    private float? _c;

    /// <summary>
    /// Whether a linear analog sensor's reading is filtered, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 F
    /// </remarks>
    public bool? Filtered
    {
        get => _filtered;
        set => SetPropertyValue(ref _filtered, value);
    }
    private bool? _filtered;

    /// <summary>
    /// High sensor reading: the reading at the top of a linear analog sensor's range (otherwise null)
    /// </summary>
    public float? HighReading
    {
        get => _highReading;
        set => SetPropertyValue(ref _highReading, value);
    }
    private float? _highReading;

    /// <summary>
    /// Last sensor reading (in C) or null if invalid
    /// </summary>
    [Live]
    public float? LastReading
    {
        get => _lastReading;
        set => SetPropertyValue(ref _lastReading, value);
    }
    private float? _lastReading;

    /// <summary>
    /// Low sensor reading: the reading at the bottom of a linear analog sensor's range (otherwise null)
    /// </summary>
    public float? LowReading
    {
        get => _lowReading;
        set => SetPropertyValue(ref _lowReading, value);
    }
    private float? _lowReading;

    /// <summary>
    /// Mains frequency (in Hz) an RTD or thermocouple sensor's converter rejects, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 F
    /// </remarks>
    public int? MainsFrequency
    {
        get => _mainsFrequency;
        set => SetPropertyValue(ref _mainsFrequency, value);
    }
    private int? _mainsFrequency;

    /// <summary>
    /// Name of this sensor or null if not configured
    /// </summary>
    public string? Name
    {
        get => _name;
        set => SetPropertyValue(ref _name, value);
    }
    private string? _name;

    /// <summary>
    /// Share of a thermopile sensor's object output mixed into its reading, in 256ths, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 F
    /// </remarks>
    public int? ObjectMix
    {
        get => _objectMix;
        set => SetPropertyValue(ref _objectMix, value);
    }
    private int? _objectMix;

    /// <summary>
    /// Offset adjustment (in K)
    /// </summary>
    /// <remarks>
    /// See also M308 U
    /// </remarks>
    public float OffsetAdj
    {
        get => _offsetAdj;
        set => SetPropertyValue(ref _offsetAdj, value);
    }
    private float _offsetAdj = 0F;

    /// <summary>
    /// Port of this sensor or null if not applicable
    /// </summary>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// Resistance at 25C of a thermistor, or of a thermopile sensor's environment thermistor
    /// </summary>
    public float? R25
    {
        get => _r25;
        set => SetPropertyValue(ref _r25, value);
    }
    private float? _r25;

    /// <summary>
    /// Radiation exponent of a thermopile sensor, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 R
    /// </remarks>
    public float? RadiationExponent
    {
        get => _radiationExponent;
        set => SetPropertyValue(ref _radiationExponent, value);
    }
    private float? _radiationExponent;

    /// <summary>
    /// Series resistance of this sensor channel (only applicable for thermistors)
    /// </summary>
    public float? RRef
    {
        get => _rRef;
        set => SetPropertyValue(ref _rRef, value);
    }
    private float? _rRef;

    /// <summary>
    /// Slope adjustment factor
    /// </summary>
    /// <remarks>
    /// See also M308 V
    /// </remarks>
    public float SlopeAdj
    {
        get => _slopeAdj;
        set => SetPropertyValue(ref _slopeAdj, value);
    }
    private float _slopeAdj = 0F;

    /// <summary>
    /// State of this sensor
    /// </summary>
    public TemperatureError State
    {
        get => _state;
        set => SetPropertyValue(ref _state, value);
    }
    private TemperatureError _state = TemperatureError.Ok;

    /// <summary>
    /// Type letter of a MAX31856 thermocouple, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 K
    /// </remarks>
    public string? ThermocoupleType
    {
        get => _thermocoupleType;
        set => SetPropertyValue(ref _thermocoupleType, value);
    }
    private string? _thermocoupleType;

    /// <summary>
    /// Type of this sensor
    /// </summary>
    public AnalogSensorType Type
    {
        get => _type;
        set => SetPropertyValue(ref _type, value);
    }
    private AnalogSensorType _type = AnalogSensorType.Unknown;

    /// <summary>
    /// Number of wires an RTD sensor is connected with, or null if not set
    /// </summary>
    /// <remarks>
    /// See also M308 W
    /// </remarks>
    public int? Wires
    {
        get => _wires;
        set => SetPropertyValue(ref _wires, value);
    }
    private int? _wires;
}
