namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about the current move
/// </summary>
public partial class CurrentMove : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Acceleration of the current move (in mm/s^2)
    /// </summary>
    [Live]
    public float Acceleration
    {
        get => _acceleration;
        set => SetPropertyValue(ref _acceleration, value);
    }
    private float _acceleration;

    /// <summary>
    /// Deceleration of the current move (in mm/s^2)
    /// </summary>
    [Live]
    public float Deceleration
    {
        get => _deceleration;
        set => SetPropertyValue(ref _deceleration, value);
    }
    private float _deceleration;

    /// <summary>
    /// Total distance of the current move (in mm)
    /// </summary>
    [Live]
    public float Distance
    {
        get => _distance;
        set => SetPropertyValue(ref _distance, value);
    }
    private float _distance;

    /// <summary>
    /// Duration of the current move (in s)
    /// </summary>
    [Live]
    public float Duration
    {
        get => _duration;
        set => SetPropertyValue(ref _duration, value);
    }
    private float _duration;

    /// <summary>
    /// Current extrusion rate (in mm/s)
    /// </summary>
    [Live]
    public float ExtrusionRate
    {
        get => _extrusionRate;
        set => SetPropertyValue(ref _extrusionRate, value);
    }
    private float _extrusionRate;

    /// <summary>
    /// Position in the job file of the move being executed (in bytes or null)
    /// </summary>
    [Live]
    public long? FilePosition
    {
        get => _filePosition;
        set => SetPropertyValue(ref _filePosition, value);
    }
    private long? _filePosition;

    /// <summary>
    /// Laser PWM of the current move (0..1) or null if not applicable
    /// </summary>
    [Live]
    public float? LaserPwm
    {
        get => _laserPwm;
        set => SetPropertyValue(ref _laserPwm, value);
    }
    private float? _laserPwm = null;

    /// <summary>
    /// Requested speed of the current move (in mm/s)
    /// </summary>
    [Live]
    public float RequestedSpeed
    {
        get => _requestedSpeed;
        set => SetPropertyValue(ref _requestedSpeed, value);
    }
    private float _requestedSpeed;

    /// <summary>
    /// Top speed of the current move (in mm/s)
    /// </summary>
    [Live]
    public float TopSpeed
    {
        get => _topSpeed;
        set => SetPropertyValue(ref _topSpeed, value);
    }
    private float _topSpeed;
}
