namespace DuetAPI.ObjectModel;

/// <summary>
/// Configured (M569.1) closed loop settings of a driver
/// </summary>
/// <remarks>
/// A null value has not been configured, so the board applies its own default
/// </remarks>
public partial class DriverClosedLoopConfig : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Acceleration feedforward constant (M569.1 A)
    /// </summary>
    public float? AccelerationFeedForward
    {
        get => _accelerationFeedForward;
        set => SetPropertyValue(ref _accelerationFeedForward, value);
    }
    private float? _accelerationFeedForward;

    /// <summary>
    /// Encoder counts per revolution of a quadrature or linear composite encoder (M569.1 C)
    /// </summary>
    public float? CountsPerRev
    {
        get => _countsPerRev;
        set => SetPropertyValue(ref _countsPerRev, value);
    }
    private float? _countsPerRev;

    /// <summary>
    /// Standstill position error deadband in full steps, or null for the automatic deadband (M569.1 B)
    /// </summary>
    public float? Deadband
    {
        get => _deadband;
        set => SetPropertyValue(ref _deadband, value);
    }
    private float? _deadband;

    /// <summary>
    /// Encoder the driver reads its position from (M569.1 T)
    /// </summary>
    public EncoderType EncoderType
    {
        get => _encoderType;
        set => SetPropertyValue(ref _encoderType, value);
    }
    private EncoderType _encoderType;

    /// <summary>
    /// Position error in full steps above which a driver error is reported (second value of M569.1 E)
    /// </summary>
    public float? ErrorThreshold
    {
        get => _errorThreshold;
        set => SetPropertyValue(ref _errorThreshold, value);
    }
    private float? _errorThreshold;

    /// <summary>
    /// Derivative constant (M569.1 D)
    /// </summary>
    public float? Kd
    {
        get => _kd;
        set => SetPropertyValue(ref _kd, value);
    }
    private float? _kd;

    /// <summary>
    /// Integral constant (M569.1 I)
    /// </summary>
    public float? Ki
    {
        get => _ki;
        set => SetPropertyValue(ref _ki, value);
    }
    private float? _ki;

    /// <summary>
    /// Proportional constant (M569.1 R)
    /// </summary>
    public float? Kp
    {
        get => _kp;
        set => SetPropertyValue(ref _kp, value);
    }
    private float? _kp;

    /// <summary>
    /// Type of the magnetic encoder, such as "as5047d" (M569.1 Y)
    /// </summary>
    public string? MagneticEncoderType
    {
        get => _magneticEncoderType;
        set => SetPropertyValue(ref _magneticEncoderType, value);
    }
    private string? _magneticEncoderType;

    /// <summary>
    /// Motor full steps per revolution (M569.1 S)
    /// </summary>
    public int? StepsPerRev
    {
        get => _stepsPerRev;
        set => SetPropertyValue(ref _stepsPerRev, value);
    }
    private int? _stepsPerRev;

    /// <summary>
    /// Motor torque in newton-metres per amp of peak motor current (M569.1 Q)
    /// </summary>
    public float? TorqueConstant
    {
        get => _torqueConstant;
        set => SetPropertyValue(ref _torqueConstant, value);
    }
    private float? _torqueConstant;

    /// <summary>
    /// Velocity feedforward constant (M569.1 V)
    /// </summary>
    public float? VelocityFeedForward
    {
        get => _velocityFeedForward;
        set => SetPropertyValue(ref _velocityFeedForward, value);
    }
    private float? _velocityFeedForward;

    /// <summary>
    /// Position error in full steps above which a driver warning is reported (first value of M569.1 E)
    /// </summary>
    public float? WarningThreshold
    {
        get => _warningThreshold;
        set => SetPropertyValue(ref _warningThreshold, value);
    }
    private float? _warningThreshold;
}
