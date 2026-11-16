namespace DuetAPI.ObjectModel;

/// <summary>
/// Details about a general-purpose output port
/// </summary>
public partial class GpOutputPort : ModelObject, IStaticModelObject
{
    /// <summary>PWM frequency of an output created by M950 P without Q (in Hz)</summary>
    /// <remarks>RepRapFirmware's <c>DefaultPinWritePwmFreq</c></remarks>
    public const int DefaultFrequency = 500;

    /// <summary>Refresh frequency of a servo created by M950 S without Q (in Hz)</summary>
    /// <remarks>
    /// A servo is driven by the width of its pulse rather than the duty cycle, and RepRapFirmware's
    /// <c>DefaultServoRefreshFrequency</c> is the rate those pulses are repeated at
    /// </remarks>
    public const int DefaultServoFrequency = 50;

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
    /// Whether this port was created as a servo by M950 S rather than as an output by M950 P
    /// </summary>
    /// <remarks>
    /// The board drives a servo pin differently from a plain PWM output, so the port cannot be
    /// recreated without knowing which it is
    /// </remarks>
    public bool Servo
    {
        get => _servo;
        set => SetPropertyValue(ref _servo, value);
    }
    private bool _servo;

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
