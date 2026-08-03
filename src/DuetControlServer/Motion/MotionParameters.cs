using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// The machine description in the form the move planner needs it, derived from the object model
/// </summary>
/// <remarks>
/// <para>
/// The object model is the configuration: <c>move.axes[]</c>, <c>move.extruders[]</c>,
/// <c>move.kinematics</c> and the rest are what M-codes write and what every API reads. This is a
/// snapshot of the subset the planner uses, rebuilt whenever that configuration changes.
/// </para>
/// <para>
/// It exists for two reasons and neither is duplication of state. First, units: the object model is
/// in the units its properties are documented in - mm/min for speeds and jerk, mm/s^2 for
/// acceleration - and the planner works in mm per step clock, so the conversion happens once here
/// rather than per move. Second, indexing: the planner addresses drives densely by logical drive
/// number, where axes count up from zero and extruders count down from the top, and walking two
/// object model collections to find a drive on the move path would mean holding the model lock
/// while planning.
/// </para>
/// <para>
/// Nothing here is authoritative. Anything that changes it changes the object model and rebuilds
/// this from it
/// </para>
/// </remarks>
internal sealed class MotionParameters
{
    /// <summary>Number of logical drives</summary>
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>Seconds per minute, for the object model's mm/min speeds</summary>
    private const float SecondsPerMinute = 60.0f;

    /// <summary>
    /// Acceleration cap to use before a motion system exists to carry one, in mm/s^2
    /// </summary>
    /// <remarks>
    /// Matches the object model's own default for <see cref="MotionSystem.PrintingAcceleration"/>, so
    /// the limit does not change the moment the first motion system appears
    /// </remarks>
    private const float DefaultAcceleration = 10000.0f;

    /// <summary>Axes the user can refer to</summary>
    public int NumAxes { get; private init; }

    /// <summary>Number of extruders</summary>
    public int NumExtruders { get; private init; }

    /// <summary>The machine geometry</summary>
    public KinematicsEngine Geometry { get; private init; } = CoreKinematicsEngine.TryCreate("cartesian")!;

    /// <summary>Microsteps per mm, by logical drive</summary>
    public float[] StepsPerMm { get; } = new float[NumDrives];

    /// <summary>Maximum speed in mm per step clock, by logical drive</summary>
    public float[] MaxFeedrates { get; } = new float[NumDrives];

    /// <summary>Maximum acceleration in mm per step clock squared, by logical drive</summary>
    public float[] Accelerations { get; } = new float[NumDrives];

    /// <summary>Reduced acceleration for probing and stall homing, by logical drive</summary>
    public float[] ReducedAccelerations { get; } = new float[NumDrives];

    /// <summary>Pressure advance in step clocks, by logical drive. Zero for anything but an extruder</summary>
    public float[] PressureAdvanceClocks { get; } = new float[NumDrives];

    /// <summary>
    /// Instantaneous speed change allowed at a junction, mm per step clock, by logical drive
    /// </summary>
    /// <remarks>
    /// The native planner has its own copy for lookahead. This one is for the acceleration cap that
    /// pressure advance imposes, which is worked out while the move is being built
    /// </remarks>
    public float[] InstantDvs { get; } = new float[NumDrives];

    /// <summary>Axes that translate rather than rotate, as a bitmap</summary>
    public uint LinearAxes { get; private init; }

    /// <summary>Axes that rotate, as a bitmap</summary>
    public uint RotationalAxes { get; private init; }

    /// <summary>Maximum acceleration for a printing move, mm per step clock squared (M204 P)</summary>
    public float MaxPrintingAcceleration { get; private init; }

    /// <summary>Maximum acceleration for a travel move, mm per step clock squared (M204 T)</summary>
    public float MaxTravelAcceleration { get; private init; }

    /// <summary>Slowest a move may run, mm per step clock</summary>
    public float MinFeedrate { get; private init; }

    /// <summary>
    /// First logical drive that is an extruder
    /// </summary>
    public int FirstExtruderDrive => NumDrives - NumExtruders;

    /// <summary>
    /// The logical drive an extruder occupies
    /// </summary>
    /// <param name="extruder">Extruder number</param>
    /// <returns>Logical drive number</returns>
    public static int ExtruderToDrive(int extruder) => NumDrives - 1 - extruder;

    /// <summary>
    /// The extruder occupying a logical drive, or -1
    /// </summary>
    /// <param name="drive">Logical drive number</param>
    /// <returns>Extruder number</returns>
    public int DriveToExtruder(int drive)
    {
        int extruder = NumDrives - 1 - drive;
        return extruder >= 0 && extruder < NumExtruders ? extruder : -1;
    }

    private MotionParameters()
    {
        // Never zero: this divides when converting motor steps back to a position, and an
        // unconfigured drive must not turn a position into an infinity
        Array.Fill(StepsPerMm, 1.0f);
    }

    /// <summary>
    /// What the machine looks like before the object model has been populated
    /// </summary>
    /// <returns>An empty snapshot</returns>
    /// <remarks>
    /// No axes and no extruders, so nothing can be planned. That is the honest state before config.g
    /// has run, and it fails by refusing to move rather than by moving something unconfigured
    /// </remarks>
    public static MotionParameters CreateDefault() => new();

    /// <summary>
    /// Take a snapshot of the object model's motion configuration
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <returns>The snapshot</returns>
    /// <remarks>The caller must hold at least a read lock on the object model</remarks>
    public static MotionParameters FromObjectModel(Move move)
    {
        int numAxes = Math.Min(move.Axes.Count, MotionLimits.MaxAxes);
        int numExtruders = Math.Min(move.Extruders.Count, MotionLimits.MaxExtruders);

        // Axes count up from zero and extruders down from the top, so more of them than the drive
        // space holds would make a drive both at once
        if (numAxes + numExtruders > NumDrives)
        {
            numExtruders = Math.Max(0, NumDrives - numAxes);
        }

        uint linearAxes = 0, rotationalAxes = 0;
        for (int axis = 0; axis < numAxes; axis++)
        {
            if (move.Axes[axis].Rotational)
            {
                rotationalAxes |= 1u << axis;
            }
            else
            {
                linearAxes |= 1u << axis;
            }
        }

        float clockSquared = MotionLimits.StepClockRate * MotionLimits.StepClockRate;

        // M204 is per motion system, which is where the object model keeps it. The planner is not
        // per motion system yet, so the first one sets the limits for all of them
        MotionSystem? motionSystem = move.MotionSystems.Count > 0 ? move.MotionSystems[0] : null;

        MotionParameters parameters = new()
        {
            NumAxes = numAxes,
            NumExtruders = numExtruders,
            Geometry = BuildGeometry(move.Kinematics),
            LinearAxes = linearAxes,
            RotationalAxes = rotationalAxes,
            MaxPrintingAcceleration = (motionSystem?.PrintingAcceleration ?? DefaultAcceleration) / clockSquared,
            MaxTravelAcceleration = (motionSystem?.TravelAcceleration ?? DefaultAcceleration) / clockSquared,
            MinFeedrate = move.MinimumMovementSpeed / MotionLimits.StepClockRate
        };

        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis a = move.Axes[axis];
            parameters.StepsPerMm[axis] = a.StepsPerMm;
            parameters.MaxFeedrates[axis] = a.Speed / SecondsPerMinute / MotionLimits.StepClockRate;
            parameters.Accelerations[axis] = a.Acceleration / clockSquared;
            parameters.ReducedAccelerations[axis] = (a.ReducedAcceleration > 0.0f ? a.ReducedAcceleration : a.Acceleration) / clockSquared;
            parameters.InstantDvs[axis] = a.Jerk / SecondsPerMinute / MotionLimits.StepClockRate;
        }

        for (int extruder = 0; extruder < numExtruders; extruder++)
        {
            Extruder e = move.Extruders[extruder];
            int drive = ExtruderToDrive(extruder);
            parameters.StepsPerMm[drive] = e.StepsPerMm;
            parameters.MaxFeedrates[drive] = e.Speed / SecondsPerMinute / MotionLimits.StepClockRate;
            parameters.Accelerations[drive] = e.Acceleration / clockSquared;
            parameters.ReducedAccelerations[drive] = e.Acceleration / clockSquared;

            parameters.InstantDvs[drive] = e.Jerk / SecondsPerMinute / MotionLimits.StepClockRate;

            // Pressure advance is a time, so it converts to step clocks rather than dividing by them
            parameters.PressureAdvanceClocks[drive] = e.PressureAdvance * MotionLimits.StepClockRate;
        }

        return parameters;
    }

    /// <summary>
    /// Build the geometry engine described by the object model's kinematics
    /// </summary>
    /// <param name="kinematics">The configured kinematics</param>
    /// <returns>The engine, falling back to Cartesian if the geometry is not supported yet</returns>
    private static KinematicsEngine BuildGeometry(DuetAPI.ObjectModel.Kinematics kinematics)
    {
        if (kinematics is CoreKinematics core)
        {
            // The matrix in the object model is authoritative when it is there: M669 can set an
            // arbitrary one, which is the whole point of the matrix form
            float[][] inverse = new float[core.InverseMatrix.Count][];
            for (int i = 0; i < inverse.Length; i++)
            {
                inverse[i] = core.InverseMatrix[i];
            }

            if (inverse.Length > 0)
            {
                CoreKinematicsEngine engine = new(core.Name.ToString(), inverse);
                if (engine.IsValid)
                {
                    return engine;
                }
            }
        }

        // Delta, SCARA, polar and hangprinter are not ported yet. Falling back to Cartesian keeps
        // the planner working on a machine it can describe rather than refusing to plan at all
        return CoreKinematicsEngine.TryCreate(kinematics.Name.ToString())
               ?? CoreKinematicsEngine.TryCreate("cartesian")!;
    }

    /// <summary>
    /// Build the description to push down to the native motion engine
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <returns>The native configuration</returns>
    /// <remarks>The caller must hold at least a read lock on the object model</remarks>
    public MotionConfig ToMotionConfig(Move move)
    {
        MoveQueueItem? queue = move.Queue.Count > 0 ? move.Queue[0] : null;

        MotionConfig config = new()
        {
            NumVisibleAxes = (byte)NumAxes,
            NumTotalAxes = (byte)NumAxes,
            NumExtruders = (byte)NumExtruders,
            NumRings = (byte)Math.Max(1, Math.Min(move.MotionSystems.Count, MotionLimits.MaxRings)),
            NumDdasPerRing = (ushort)(queue is not null && queue.Length > 0 ? queue.Length : 40),
            GracePeriodMs = (uint)MathF.Round((queue?.GracePeriod ?? 0.01f) * 1000.0f),
            JerkPolicy = (uint)move.JerkPolicy,
            BacklashCorrectionDistanceFactor = (uint)Math.Max(1, move.BacklashFactor)
        };

        uint continuousRotationAxes = 0;
        for (int axis = 0; axis < NumAxes; axis++)
        {
            Axis a = move.Axes[axis];
            if (a.Rotational && a.ContinuousRotation)
            {
                continuousRotationAxes |= 1u << axis;
            }

            config.DriveStepsPerMm[axis] = a.StepsPerMm;

            // Jerk is an instantaneous speed change, so it converts like a speed
            config.InstantDvs[axis] = a.Jerk / SecondsPerMinute / MotionLimits.StepClockRate;
            config.PrintingInstantDvs[axis] = a.PrintingJerk / SecondsPerMinute / MotionLimits.StepClockRate;

            config.BacklashSteps[axis] = (int)MathF.Round(a.Backlash * a.StepsPerMm);
            config.ControllingDrives[axis] = Geometry.GetControllingDrives(axis);

            DriverId[] drivers = new DriverId[a.Drivers.Count];
            for (int i = 0; i < drivers.Length; i++)
            {
                drivers[i] = ToNativeDriver(a.Drivers[i]);
            }
            config.AxisDrivers[axis] = AxisDriversConfig.WithDrivers(drivers);
        }
        config.ContinuousRotationAxes = continuousRotationAxes;

        for (int extruder = 0; extruder < NumExtruders; extruder++)
        {
            Extruder e = move.Extruders[extruder];
            int drive = ExtruderToDrive(extruder);

            config.DriveStepsPerMm[drive] = e.StepsPerMm;
            config.InstantDvs[drive] = e.Jerk / SecondsPerMinute / MotionLimits.StepClockRate;
            config.PrintingInstantDvs[drive] = e.PrintingJerk / SecondsPerMinute / MotionLimits.StepClockRate;
            config.PressureAdvanceClocks[drive] = e.PressureAdvance * MotionLimits.StepClockRate;
            config.ExtruderDrivers[extruder] = e.Driver is not null ? ToNativeDriver(e.Driver) : DriverId.None;
        }

        return config;
    }

    /// <summary>
    /// Convert an object model driver id to the native one
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <returns>The native driver id</returns>
    private static DriverId ToNativeDriver(DuetAPI.Utility.DriverId driver)
        => new((byte)driver.Board, (byte)driver.Port);
}
