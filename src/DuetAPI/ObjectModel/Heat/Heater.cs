namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a heater
/// </summary>
public partial class Heater : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Active temperature of the heater (in C)
    /// </summary>
    [Live]
    public float Active
    {
        get => _active;
        set => SetPropertyValue(ref _active, value);
    }
    private float _active;

    /// <summary>
    /// Average heater PWM value (0..1)
    /// </summary>
    [Live]
    public float AvgPwm
    {
        get => _avgPwm;
        set => SetPropertyValue(ref _avgPwm, value);
    }
    private float _avgPwm;

    /// <summary>
    /// Current temperature of the heater (in C)
    /// </summary>
    [Live]
    public float Current
    {
        get => _current;
        set => SetPropertyValue(ref _current, value);
    }
    private float _current = -273.15F;

    /// <summary>
    /// Current feedforward PWM boost applied to the heater
    /// </summary>
    public float? ExtrPwmBoost
    {
        get => _extrPwmBoost;
        set => SetPropertyValue(ref _extrPwmBoost, value);
    }
    private float? _extrPwmBoost;

    /// <summary>
    /// Current temperature boost applied to the heater
    /// </summary>
    public float? ExtrTempBoost
    {
        get => _extrTempBoost;
        set => SetPropertyValue(ref _extrTempBoost, value);
    }
    private float? _extrTempBoost;

    /// <summary>
    /// Maximum temperature allowed for this heater (in C)
    /// </summary>
    /// <remarks>
    /// This is only temporary and should be replaced by a representation of the heater protection as in RRF
    /// </remarks>
    public float Max
    {
        get => _max;
        set => SetPropertyValue(ref _max, value);
    }
    private float _max = 285F;

    /// <summary>
    /// Minimum temperature allowed for this heater (in C)
    /// </summary>
    /// <remarks>
    /// This is only temporary and should be replaced by a representation of the heater protection as in RRF
    /// </remarks>
    public float Min
    {
        get => _min;
        set => SetPropertyValue(ref _min, value);
    }
    private float _min = -10F;

    /// <summary>
    /// Maximum number of consecutive temperature reading failures before a heater fault is raised
    /// </summary>
    public int MaxBadReadings
    {
        get => _maxBadReadings;
        set => SetPropertyValue(ref _maxBadReadings, value);
    }
    private int _maxBadReadings = 3;

    /// <summary>
    /// Time for which a temperature anomaly must persist on this heater before raising a heater fault (in s)
    /// </summary>
    public float MaxHeatingFaultTime
    {
        get => _maxHeatingFaultTime;
        set => SetPropertyValue(ref _maxHeatingFaultTime, value);
    }
    private float _maxHeatingFaultTime = 5F;

    /// <summary>
    /// Permitted temperature excursion from the setpoint for this heater (in K)
    /// </summary>
    public float MaxTempExcursion
    {
        get => _maxTempExcursion;
        set => SetPropertyValue(ref _maxTempExcursion, value);
    }
    private float _maxTempExcursion = 15F;

    /// <summary>
    /// Information about the heater model
    /// </summary>
    public HeaterModel Model { get; } = new HeaterModel();

    /// <summary>
    /// Monitors of this heater
    /// </summary>
    public StaticModelCollection<HeaterMonitor> Monitors { get; } = [];

    /// <summary>
    /// Port of this heater as given to M950, or null if it has none
    /// </summary>
    /// <remarks>
    /// The output that drives the heating element, which is not the same as the port of the sensor
    /// that reads it - <see cref="Sensor"/> names that one. Both are needed to recreate the machine:
    /// the sensor says which board the heater is on, and this says what that board should drive
    /// </remarks>
    public string? Port
    {
        get => _port;
        set => SetPropertyValue(ref _port, value);
    }
    private string? _port;

    /// <summary>
    /// PWM frequency of this heater in Hz
    /// </summary>
    /// <remarks>
    /// Set by M950 H Q. A mains heater switched by a relay wants a low frequency and a cartridge
    /// heater on a MOSFET a high one, so it is part of how the machine is wired rather than a default
    /// </remarks>
    public float Frequency
    {
        get => _frequency;
        set => SetPropertyValue(ref _frequency, value);
    }
    private float _frequency;

    /// <summary>
    /// Sensor number of this heater or -1 if not configured
    /// </summary>
    public int Sensor
    {
        get => _sensor;
        set => SetPropertyValue(ref _sensor, value);
    }
    private int _sensor = -1;

    /// <summary>
    /// Standby temperature of the heater (in C)
    /// </summary>
    [Live]
    public float Standby
    {
        get => _standby;
        set => SetPropertyValue(ref _standby, value);
    }
    private float _standby;

    /// <summary>
    /// State of the heater
    /// </summary>
    [Live]
    public HeaterState State
    {
        get => _state;
        set => SetPropertyValue(ref _state, value);
    }
    private HeaterState _state = HeaterState.Off;
}
