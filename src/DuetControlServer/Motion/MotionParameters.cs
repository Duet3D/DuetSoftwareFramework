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
    /// <returns>The engine, falling back to Cartesian if the geometry cannot be described</returns>
    /// <remarks>
    /// The object model does not hold every parameter every geometry has - it reports what
    /// RepRapFirmware reports, and RepRapFirmware keeps some of it to itself. Where a parameter is
    /// missing the engine takes the same default RepRapFirmware would have before the M-code that
    /// sets it has been seen, so a machine that has not been configured behaves as an unconfigured
    /// machine of that kind rather than as some other kind of machine
    /// </remarks>
    private static KinematicsEngine BuildGeometry(DuetAPI.ObjectModel.Kinematics kinematics)
    {
        switch (kinematics)
        {
            case CoreKinematics core:
                {
                    // The matrix in the object model is authoritative when it is there: M669 can set
                    // an arbitrary one, which is the whole point of the matrix form
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
                    break;
                }

            case DeltaKinematics delta:
                return BuildDelta(delta);

            case HangprinterKinematics hangprinter:
                return BuildHangprinter(hangprinter);

            case ScaraKinematics scara:
                // Both SCARA geometries share one object model class, because that is how
                // RepRapFirmware reports them. Only the name tells them apart
                return scara.Name == KinematicsName.FiveBarScara ? BuildFiveBarScara() : BuildScara(scara);

            case PolarKinematics polar:
                return BuildPolar(polar);
        }

        // A rotary delta reports nothing but its name, so there is no derived class to match on
        if (kinematics.Name == KinematicsName.RotaryDelta)
        {
            return new RotaryDeltaKinematicsEngine();
        }

        return CoreKinematicsEngine.TryCreate(kinematics.Name.ToString())
               ?? CoreKinematicsEngine.TryCreate("cartesian")!;
    }

    /// <summary>
    /// Build a delta geometry from the object model
    /// </summary>
    /// <param name="delta">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine BuildDelta(DeltaKinematics delta)
    {
        int numTowers = Math.Clamp(delta.Towers.Count, LinearDeltaKinematicsEngine.UsualNumTowers, LinearDeltaKinematicsEngine.MaxTowers);

        float[] diagonals = new float[numTowers];
        float[] endstopAdjustments = new float[numTowers];
        float[] angleCorrections = new float[LinearDeltaKinematicsEngine.UsualNumTowers];

        for (int tower = 0; tower < numTowers; tower++)
        {
            DeltaTower? configured = tower < delta.Towers.Count ? delta.Towers[tower] : null;
            diagonals[tower] = configured is not null && configured.Diagonal > 0.0f
                ? configured.Diagonal
                : LinearDeltaKinematicsEngine.DefaultDiagonal;
            endstopAdjustments[tower] = configured?.EndstopAdjustment ?? 0.0f;
            if (tower < LinearDeltaKinematicsEngine.UsualNumTowers)
            {
                angleCorrections[tower] = configured?.AngleCorrection ?? 0.0f;
            }
        }

        // A delta radius of zero would put all three towers on top of each other, which is not a
        // machine - so it means M665 has not run rather than that the towers are really there
        float radius = delta.DeltaRadius > 0.0f ? delta.DeltaRadius : LinearDeltaKinematicsEngine.DefaultDeltaRadius;
        float printRadius = delta.PrintRadius > 0.0f ? delta.PrintRadius : LinearDeltaKinematicsEngine.DefaultPrintRadius;

        return new LinearDeltaKinematicsEngine(
            numTowers, radius, diagonals, angleCorrections, endstopAdjustments,
            delta.HomedHeight, printRadius, delta.XTilt, delta.YTilt);
    }

    /// <summary>
    /// Build a SCARA geometry from the object model
    /// </summary>
    /// <param name="scara">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine BuildScara(ScaraKinematics scara)
    {
        float[] thetaLimits = [.. scara.ThetaLimits];
        float[] psiLimits = [.. scara.PsiLimits];
        float[] crosstalk = [.. scara.Crosstalk];

        float proximal = scara.ProximalLength > 0.0f ? scara.ProximalLength : ScaraKinematicsEngine.DefaultProximalArmLength;
        float distal = scara.DistalLength > 0.0f ? scara.DistalLength : ScaraKinematicsEngine.DefaultDistalArmLength;

        // Both limits at zero means the joint cannot turn at all, which is the object model's default
        // rather than a real configuration
        if (thetaLimits.Length < 2 || (thetaLimits[0] == 0.0f && thetaLimits[1] == 0.0f))
        {
            thetaLimits = [ScaraKinematicsEngine.DefaultMinTheta, ScaraKinematicsEngine.DefaultMaxTheta];
        }
        if (psiLimits.Length < 2 || (psiLimits[0] == 0.0f && psiLimits[1] == 0.0f))
        {
            psiLimits = [ScaraKinematicsEngine.DefaultMinPsi, ScaraKinematicsEngine.DefaultMaxPsi];
        }

        return new ScaraKinematicsEngine(
            proximal, distal, thetaLimits, psiLimits, crosstalk,
            scara.XOffset, scara.YOffset, scara.MinRadius);
    }

    /// <summary>
    /// Build a five-bar parallel SCARA geometry
    /// </summary>
    /// <returns>The engine</returns>
    /// <remarks>
    /// Nothing in the object model describes this geometry - RepRapFirmware reports only its name -
    /// so the engine is built with the defaults its M669 documentation gives. Once M669 is handled on
    /// this side the parameters it carries should be passed through here
    /// </remarks>
    private static KinematicsEngine BuildFiveBarScara()
        => new FiveBarScaraKinematicsEngine(
            xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
            proximalL: 100.0f, proximalR: 100.0f,
            distalL: 100.0f, distalR: 100.0f);

    /// <summary>
    /// Build a polar geometry from the object model
    /// </summary>
    /// <param name="polar">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine BuildPolar(PolarKinematics polar)
    {
        float clockSquared = MotionLimits.StepClockRate * MotionLimits.StepClockRate;

        float maxRadius = polar.RadiusMax > 0.0f ? polar.RadiusMax : PolarKinematicsEngine.DefaultMaxRadius;
        float maxSpeed = polar.TTSpeedMax > 0.0f ? polar.TTSpeedMax : PolarKinematicsEngine.DefaultMaxTurntableSpeed;
        float maxAcceleration = polar.TTAccMax > 0.0f ? polar.TTAccMax : PolarKinematicsEngine.DefaultMaxTurntableAcceleration;

        // The object model keeps the turntable limits per second, as the M669 that sets them does.
        // The planner works in step clocks, so they convert once here rather than on every move
        return new PolarKinematicsEngine(
            polar.RadiusMin, maxRadius, polar.RadiusHomed,
            maxSpeed / MotionLimits.StepClockRate,
            maxAcceleration / clockSquared);
    }

    /// <summary>
    /// Build a hangprinter geometry from the object model
    /// </summary>
    /// <param name="hangprinter">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine BuildHangprinter(HangprinterKinematics hangprinter)
    {
        if (hangprinter.Anchors.Count < 3)
        {
            return HangprinterKinematicsEngine.CreateDefault();
        }

        float[][] anchors = new float[hangprinter.Anchors.Count][];
        for (int i = 0; i < anchors.Length; i++)
        {
            anchors[i] = hangprinter.Anchors[i];
        }

        float printRadius = hangprinter.PrintRadius > 0.0f ? hangprinter.PrintRadius : HangprinterKinematicsEngine.DefaultPrintRadius;
        return new HangprinterKinematicsEngine(anchors, printRadius);
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
        // Some geometries have an axis that goes round whether or not M208 said so - a polar bed and
        // a SCARA joint with more than a full circle of travel both do - so the geometry gets to add
        // to what the configuration declared, masked to the axes that exist
        config.ContinuousRotationAxes = (continuousRotationAxes | Geometry.ContinuousRotationAxes)
                                        & (NumAxes >= 32 ? uint.MaxValue : (1u << NumAxes) - 1);

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
