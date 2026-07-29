using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about the move subsystem
/// </summary>
public partial class Move : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Value of the M201 T parameter. Only present in builds that support S-curve acceleration
    /// </summary>
    public float? AccelerationTime
    {
        get => _accelerationTime;
        set => SetPropertyValue(ref _accelerationTime, value);
    }
    private float? _accelerationTime;

    /// <summary>
    /// List of the configured axes
    /// </summary>
    /// <seealso cref="Axis"/>
    [Live]
    [LimitedResponseCount(9)]
    public StaticModelCollection<Axis> Axes { get; } = [];

    /// <summary>
    /// Backlash distance multiplier
    /// </summary>
    public int BacklashFactor
    {
        get => _backlashFactor;
        set => SetPropertyValue(ref _backlashFactor, value);
    }
    private int _backlashFactor = 10;

    /// <summary>
    /// Information about the automatic calibration
    /// </summary>
    public MoveCalibration Calibration { get; } = new MoveCalibration();

    /// <summary>
    /// Information about the currently configured compensation options
    /// </summary>
    public MoveCompensation Compensation { get; } = new MoveCompensation();
    
    /// <summary>
    /// Information about the current move
    /// </summary>
    [Live]
    public CurrentMove CurrentMove { get; } = new CurrentMove();

    /// <summary>
    /// List of configured extruders
    /// </summary>
    /// <seealso cref="Extruder"/>
    [Live]
    public StaticModelCollection<Extruder> Extruders { get; } = [];
    
    /// <summary>
    /// Idle current reduction parameters
    /// </summary>
    public MotorsIdleControl Idle { get; } = new MotorsIdleControl();

    /// <summary>
    /// How aggressively moves may be melded into each other (M566 P)
    /// </summary>
    /// <remarks>
    /// 0 allows a junction speed only between moves of the same kind - both printing, or both
    /// travel; higher values allow melding across those boundaries. Read by the planner's lookahead
    /// </remarks>
    public int JerkPolicy
    {
        get => _jerkPolicy;
        set => SetPropertyValue(ref _jerkPolicy, value);
    }
    private int _jerkPolicy;

    /// <summary>
    /// Slowest a move is allowed to run (in mm/s)
    /// </summary>
    /// <remarks>
    /// A floor rather than a preference: the planner's timing arithmetic overflows for a move slow
    /// enough to take more than about 2^31 step clocks
    /// </remarks>
    public float MinimumMovementSpeed
    {
        get => _minimumMovementSpeed;
        set => SetPropertyValue(ref _minimumMovementSpeed, value);
    }
    private float _minimumMovementSpeed = 0.5F;

    /// <summary>
    /// List of configured keep-out zones
    /// </summary>
    public StaticModelCollection<KeepoutZone> Keepout { get; } = [];

    /// <summary>
    /// Configured kinematics options
    /// </summary>
    public Kinematics Kinematics
    {
        get => _kinematics;
        set => SetPropertyValue(ref _kinematics, value);
    }
    private Kinematics _kinematics = new();

    /// <summary>
    /// Limit axis positions by their minima and maxima
    /// </summary>
    public bool LimitAxes
    {
        get => _limitAxes;
        set => SetPropertyValue(ref _limitAxes, value);
    }
    private bool _limitAxes = true;

    /// <summary>
    /// Indicates if standard moves are forbidden if the corresponding axis is not homed
    /// </summary>
    public bool NoMovesBeforeHoming
    {
        get => _noMovesBeforeHoming;
        set => SetPropertyValue(ref _noMovesBeforeHoming, value);
    }
    private bool _noMovesBeforeHoming = true;

    /// <summary>
    /// List of configured motion systems
    /// </summary>
    [Live]
    public StaticModelCollection<MotionSystem> MotionSystems { get; } = [];

    /// <summary>
    /// Maximum acceleration allowed while printing (in mm/s^2)
    /// </summary>
    [Obsolete("use motionSystems[].printingAcceleration instead")]
    public float PrintingAcceleration
    {
        get => _printingAcceleration;
        set => SetPropertyValue(ref _printingAcceleration, value);
    }
    private float _printingAcceleration = 10000F;

    /// <summary>
    /// List of move queue items (DDA rings)
    /// </summary>
    public StaticModelCollection<MoveQueueItem> Queue { get; } = [];

    /// <summary>
    /// Parameters for centre rotation
    /// </summary>
    [Obsolete("use motionSystems[].rotation instead")]
    public MoveRotation Rotation { get; } = new MoveRotation();

    /// <summary>
    /// Parameters for input shaping
    /// </summary>
    public InputShaping Shaping { get; } = new InputShaping();

    /// <summary>
    /// Speed factor applied to every regular move (0.01..1 or greater)
    /// </summary>
    public float SpeedFactor
    {
        get => _speedFactor;
        set => SetPropertyValue(ref _speedFactor, value);
    }
    private float _speedFactor = 1F;

    /// <summary>
    /// Maximum acceleration allowed while travelling (in mm/s^2)
    /// </summary>
    [Obsolete("use motionSystems[].travelAcceleration instead")]
    public float TravelAcceleration
    {
        get => _travelAcceleration;
        set => SetPropertyValue(ref _travelAcceleration, value);
    }
    private float _travelAcceleration = 10000F;

    /// <summary>
    /// Indicates if third-order S-curve acceleration is enabled.
    /// Only present in builds that support S-curve acceleration
    /// </summary>
    public bool UsingSCurve
    {
        get => _usingSCurve;
        set => SetPropertyValue(ref _usingSCurve, value);
    }
    private bool _usingSCurve;

    /// <summary>
    /// Virtual total extruder position
    /// </summary>
    [Obsolete("use motionSystems[].virtualEPos instead")]
    public float VirtualEPos
    {
        get => _virtualEPos;
        set => SetPropertyValue(ref _virtualEPos, value);
    }
    private float _virtualEPos;

    /// <summary>
    /// Index of the currently selected workplace
    /// </summary>
    [Obsolete("use motionSystems[].workplaceNumber instead")]
    public int WorkplaceNumber
    {
        get => _workplaceNumber;
        set => SetPropertyValue(ref _workplaceNumber, value);
    }
    private int _workplaceNumber;
}
