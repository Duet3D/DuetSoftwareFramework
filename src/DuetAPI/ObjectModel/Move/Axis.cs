using DuetAPI.Utility;
using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a configured axis
/// </summary>
public partial class Axis : ModelObject, IStaticModelObject
{
    /// <summary>
    /// List of supported axis letters
    /// </summary>
    public static readonly char[] Letters = [
        'X', 'Y', 'Z',
        'U', 'V', 'W',
        'A', 'B', 'C', 'D',
        'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z'
    ];

    // What an axis may do before anything has configured it. RepRapFirmware's Move::Init writes the
    // same values into every axis slot before config.g runs, so an axis that no M201, M203 or M566
    // mentions is still movable.
    //
    // Its constants are in mm/sec and mm/sec^2. Speed and jerk are carried here in mm/min, so those
    // are converted once, here, rather than a per-minute field being given a per-second number.
    //
    // The acceleration is the one that must never be left at zero: the planner works a move's
    // duration out by dividing by it, so an axis without one has every move on it rejected as
    // infinitely long and simply never moves.

    /// <summary>Speed an axis may move at until M203 says otherwise (in mm/min)</summary>
    public const float DefaultSpeed = 100F * 60F;

    /// <summary>Speed a Z axis may move at until M203 says otherwise (in mm/min)</summary>
    /// <remarks>
    /// Z gets its own, slower, defaults throughout, as it does in RepRapFirmware: it is usually a
    /// leadscrew carrying the bed or the gantry, and the speeds the other axes tolerate would damage
    /// it. The axis letter is what selects these, so they are applied where an axis is created
    /// </remarks>
    public const float DefaultZSpeed = 20F * 60F;

    /// <summary>Acceleration of an axis until M201 says otherwise (in mm/s^2)</summary>
    public const float DefaultAcceleration = 1000F;

    /// <summary>Acceleration of a Z axis until M201 says otherwise (in mm/s^2)</summary>
    public const float DefaultZAcceleration = 200F;

    /// <summary>Jerk of an axis until M566 says otherwise (in mm/min)</summary>
    public const float DefaultJerk = 15F * 60F;

    /// <summary>Jerk of a Z axis until M566 says otherwise (in mm/min)</summary>
    public const float DefaultZJerk = 10F * 60F;

    /// <summary>
    /// Acceleration of this axis (in mm/s^2)
    /// </summary>
    public float Acceleration
    {
        get => _acceleration;
        set => SetPropertyValue(ref _acceleration, value);
    }
    private float _acceleration = DefaultAcceleration;

    /// <summary>
    /// Babystep amount (in mm)
    /// </summary>
    public float Babystep
    {
        get => _babystep;
        set => SetPropertyValue(ref _babystep, value);
    }
    private float _babystep;

    /// <summary>
    /// Configured backlash of this axis (in mm)
    /// </summary>
    public float Backlash
    {
        get => _backlash;
        set => SetPropertyValue(ref _backlash, value);
    }
    private float _backlash;

    /// <summary>
    /// Motor current (in mA)
    /// </summary>
    public int Current
    {
        get => _current;
        set => SetPropertyValue(ref _current, value);
    }
    private int _current;

    /// <summary>
    /// List of the assigned drivers
    /// </summary>
    public ObservableCollection<DriverId> Drivers { get; } = [];

    /// <summary>
    /// Whether or not the axis is homed
    /// </summary>
    public bool Homed
    {
        get => _homed;
        set => SetPropertyValue(ref _homed, value);
    }
    private bool _homed;

    /// <summary>
    /// Motor jerk (in mm/min)
    /// </summary>
    public float Jerk
    {
        get => _jerk;
        set => SetPropertyValue(ref _jerk, value);
    }
    private float _jerk = DefaultJerk;

    /// <summary>
    /// Letter of this axis
    /// </summary>
    public char Letter
    {
        get => _letter;
        set => SetPropertyValue(ref _letter, value);
    }
    private char _letter;

    /// <summary>
    /// Current machine position (in mm) or null if unknown/unset
    /// </summary>
    /// <remarks>
    /// This value reflects the machine position of the move being performed or of the last one if the machine is not moving
    /// </remarks>
    [Live]
    public float? MachinePosition
    {
        get => _machinePosition;
        set => SetPropertyValue(ref _machinePosition, value);
    }
    private float? _machinePosition;

    /// <summary>
    /// Maximum travel of this axis (in mm)
    /// </summary>
    public float Max
    {
        get => _max;
        set => SetPropertyValue(ref _max, value);
    }
    private float _max = 200F;

    /// <summary>
    /// Whether the axis maximum was probed
    /// </summary>
    public bool MaxProbed
    {
        get => _maxProbed;
        set => SetPropertyValue(ref _maxProbed, value);
    }
    private bool _maxProbed;

    /// <summary>
    /// Microstepping configuration
    /// </summary>
    public Microstepping Microstepping { get; } = new Microstepping();

    /// <summary>
    /// Minimum travel of this axis (in mm)
    /// </summary>
    public float Min
    {
        get => _min;
        set => SetPropertyValue(ref _min, value);
    }
    private float _min;

    /// <summary>
    /// Whether the axis minimum was probed
    /// </summary>
    public bool MinProbed
    {
        get => _minProbed;
        set => SetPropertyValue(ref _minProbed, value);
    }
    private bool _minProbed;

    /// <summary>
    /// Percentage applied to the motor current (0..100)
    /// </summary>
    public int PercentCurrent
    {
        get => _percentCurrent;
        set => SetPropertyValue(ref _percentCurrent, value);
    }
    private int _percentCurrent = 100;

    /// <summary>
    /// Percentage applied to the motor current during standstill (0..100 or null if not supported)
    /// </summary>
    public int? PercentStstCurrent
    {
        get => _percentStstCurrent;
        set => SetPropertyValue(ref _percentStstCurrent, value);
    }
    private int? _percentStstCurrent;

    /// <summary>
    /// Whether or not the axis is currently using phase stepping
    /// </summary>
    public bool? PhaseStep
    {
        get => _phaseStep;
        set => SetPropertyValue(ref _phaseStep, value);
    }
    private bool? _phaseStep;

    /// <summary>
    /// Motor jerk during the current print only (in mm/min)
    /// </summary>
    public float PrintingJerk
    {
        get => _printingJerk;
        set => SetPropertyValue(ref _printingJerk, value);
    }
    private float _printingJerk = DefaultJerk;

    /// <summary>
    /// Whether this axis rotates rather than translates, so its units are degrees
    /// </summary>
    /// <remarks>
    /// A rotational axis takes no part in the linear distance a move covers, so the feed rate does
    /// not apply to it unless the move is rotational only. Set by the R parameter of M584
    /// </remarks>
    public bool Rotational
    {
        get => _rotational;
        set => SetPropertyValue(ref _rotational, value);
    }
    private bool _rotational;

    /// <summary>
    /// Whether this axis wraps at 360 degrees, so a move may take the short way round
    /// </summary>
    /// <remarks>Only meaningful when <see cref="Rotational"/> is set</remarks>
    public bool ContinuousRotation
    {
        get => _continuousRotation;
        set => SetPropertyValue(ref _continuousRotation, value);
    }
    private bool _continuousRotation;

    /// <summary>
    /// Reduced accelerations used by Z probing and stall homing moves (in mm/s^2)
    /// </summary>
    public float ReducedAcceleration
    {
        get => _reducedAcceleration;
        set => SetPropertyValue(ref _reducedAcceleration, value);
    }
    private float _reducedAcceleration = DefaultAcceleration;

    /// <summary>
    /// Maximum speed (in mm/min)
    /// </summary>
    public float Speed
    {
        get => _speed;
        set => SetPropertyValue(ref _speed, value);
    }
    private float _speed = DefaultSpeed;

    /// <summary>
    /// Number of microsteps per mm
    /// </summary>
    public float StepsPerMm
    {
        get => _stepsPerMm;
        set => SetPropertyValue(ref _stepsPerMm, value);
    }
    private float _stepsPerMm = 80F;

    /// <summary>
    /// Current step position of the axis (in steps)
    /// </summary>
    [Live]
    public int StepPos
    {
        get => _stepPos;
        set => SetPropertyValue(ref _stepPos, value);
    }
    private int _stepPos;

    /// <summary>
    /// Current user position (in mm) or null if unknown
    /// </summary>
    /// <remarks>
    /// This value reflects the target position of the last move fed into the look-ahead buffer
    /// </remarks>
    [Live]
    public float? UserPosition
    {
        get => _userPosition;
        set => SetPropertyValue(ref _userPosition, value);
    }
    private float? _userPosition;

    /// <summary>
    /// Whether or not the axis is visible
    /// </summary>
    public bool Visible
    {
        get => _visible;
        set => SetPropertyValue(ref _visible, value);
    }
    private bool _visible = true;

    /// <summary>
    /// Offsets of this axis for each workplace (in mm)
    /// </summary>
    public ObservableCollection<float> WorkplaceOffsets { get; } = [];
}
