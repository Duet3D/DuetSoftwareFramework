using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Acceleration cap to use before a motion system exists to carry one, in mm/s^2
    /// </summary>
    /// <remarks>
    /// Matches the object model's own default for <see cref="MotionSystem.PrintingAcceleration"/>, so
    /// the limit does not change the moment the first motion system appears
    /// </remarks>
    private const float DefaultAcceleration = 10000.0f;

    /// <summary>Moves a ring holds when the object model does not say</summary>
    private const int DefaultDdasPerRing = 40;

    /// <summary>How far ahead the engine starts a move when the object model does not say, in seconds</summary>
    private const float DefaultGracePeriodSec = 0.01f;

    /// <summary>Axes the user can refer to</summary>
    public int NumAxes { get; private init; }

    /// <summary>Number of extruders</summary>
    public int NumExtruders { get; private init; }

    /// <summary>
    /// The machine geometry
    /// </summary>
    /// <remarks>
    /// The one part of this snapshot that is not derived from the object model. The planner owns it -
    /// M665, M666 and M669 configure it and <c>move.kinematics</c> is written from it - and this holds
    /// the reference it had when the snapshot was taken. See §14 of
    /// <c>docs/devel/MCODE_MIGRATION.md</c>
    /// </remarks>
    public KinematicsEngine Geometry { get; private init; } = CoreKinematicsEngine.TryCreate(KinematicsName.Cartesian)!;

    /// <summary>
    /// The same machine as the native motion engine takes it
    /// </summary>
    /// <remarks>
    /// The second thing derived from the object model's motion configuration, built in the same walk
    /// as the rest of this class so that a setting cannot reach one and not the other. It is what
    /// <see cref="MovePlanner.ReconfigureAsync"/> serialises and pushes down
    /// </remarks>
    public MachineConfig Config { get; private init; } = new();

    /// <summary>Microsteps per mm, by logical drive</summary>
    public float[] StepsPerMm { get; } = new float[NumDrives];

    /// <summary>
    /// Which logical drive each physical driver belongs to, keyed by board and driver number
    /// </summary>
    /// <remarks>
    /// The controller names a stopped driver by the board it is on and its number there, because that
    /// is all it knows; everything on this side is indexed by logical drive. RepRapFirmware keeps the
    /// same lookup as <c>Move::GetLogicalDriveForDriver</c>
    /// </remarks>
    private readonly Dictionary<DuetAPI.Utility.DriverId, DriverPlace> _driveForDriver = [];

    /// <summary>
    /// Where a physical driver sits: which logical drive it moves, and which of that drive's drivers
    /// it is
    /// </summary>
    /// <param name="Drive">Logical drive</param>
    /// <param name="Index">Position in the drive's driver list</param>
    /// <remarks>
    /// The index matters because an axis' endstop switches are paired with its drivers by position -
    /// switch <em>i</em> stops driver <em>i</em> - so a stop report has to be attributable to one of
    /// them rather than only to the axis
    /// </remarks>
    private readonly record struct DriverPlace(int Drive, int Index);

    /// <summary>
    /// Configurations in which two drives claim the same physical driver
    /// </summary>
    /// <remarks>
    /// A driver belongs to one drive. Where a configuration says otherwise the first claim is kept -
    /// axes are walked before extruders, so an axis outranks an extruder - and the rest are recorded
    /// here for whoever rebuilt the snapshot to report. Silently letting the last claim win is what
    /// makes this worth keeping: the reverse lookup is how a stop report becomes a drive, so a
    /// homing move would then correct the position of something that was not moving and leave the
    /// axis that was to be wound back to wherever the arithmetic landed
    /// </remarks>
    public IReadOnlyList<string> DriverConflicts => _driverConflicts;
    private readonly List<string> _driverConflicts = [];

    /// <summary>
    /// Claim a physical driver for a logical drive, keeping the first claim
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <param name="drive">The drive claiming it</param>
    /// <param name="index">Its position in that drive's driver list</param>
    /// <param name="description">How to name the drive in a conflict message</param>
    private void ClaimDriver(DuetAPI.Utility.DriverId driver, int drive, int index, string description)
    {
        if (_driveForDriver.TryGetValue(driver, out DriverPlace existing))
        {
            _driverConflicts.Add($"driver {driver.Board}.{driver.Port} is assigned to {description} "
                                 + $"as well as to drive {existing.Drive}; the first assignment is used");
            return;
        }
        _driveForDriver[driver] = new DriverPlace(drive, index);
    }

    /// <summary>
    /// The logical drive a physical driver belongs to
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <returns>The drive, or -1 if no drive claims it</returns>
    public int DriveForDriver(DuetAPI.Utility.DriverId driver)
        => _driveForDriver.TryGetValue(driver, out DriverPlace place) ? place.Drive : -1;

    /// <summary>
    /// Which of its drive's drivers a physical driver is
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <returns>The index, or -1 if no drive claims it</returns>
    public int DriverIndexForDriver(DuetAPI.Utility.DriverId driver)
        => _driveForDriver.TryGetValue(driver, out DriverPlace place) ? place.Index : -1;

    /// <summary>
    /// How many physical drivers each logical drive has
    /// </summary>
    /// <remarks>
    /// An axis with a switch per driver stops its motors one at a time - that is what squares a
    /// gantry - so the endstop correction has to know when the last of them has stopped before it
    /// adopts the drive's position. RepRapFirmware reads the same count from
    /// <c>AxisDriversConfig::numDrivers</c>
    /// </remarks>
    public int[] DriversPerDrive { get; } = new int[NumDrives];

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

    // --- What a move carries ------------------------------------------------------------------
    //
    // These are not in MachineConfig: the engine holds no copy to update. MoveBuilder reads them here
    // once per move and writes them into the submission, so a change takes effect on the next move
    // built and cannot reach one that is already queued. See docs/devel/MOTION_CONFIG_ORDERING.md

    /// <summary>Instantaneous speed change allowed at a junction where both moves extrude</summary>
    public float[] PrintingInstantDvs { get; } = new float[NumDrives];

    /// <summary>Backlash to take up when a drive reverses, in microsteps</summary>
    public int[] BacklashSteps { get; } = new int[NumDrives];

    /// <summary>M592 coefficients, indexed by extruder</summary>
    public Native.NonlinearExtrusion[] NonlinearExtrusions { get; } = CreateNonlinearExtrusions();

    /// <summary>How far to spread a backlash correction, as a multiple of the backlash itself</summary>
    public uint BacklashCorrectionDistanceFactor { get; set; } = 10;

    /// <summary>M566 P: 0 allows a junction speed only between moves of the same kind</summary>
    public uint JerkPolicy { get; set; }

    /// <summary>How long the boards' input shaper spreads a move over, in step clocks</summary>
    public uint ShapingTimeClocks { get; set; }

    private static Native.NonlinearExtrusion[] CreateNonlinearExtrusions()
    {
        Native.NonlinearExtrusion[] result = new Native.NonlinearExtrusion[MotionLimits.MaxExtruders];
        Array.Fill(result, Native.NonlinearExtrusion.None);
        return result;
    }

    /// <summary>Axes that translate rather than rotate, as a bitmap</summary>
    public uint LinearAxes { get; private set; }

    /// <summary>Axes that rotate, as a bitmap</summary>
    public uint RotationalAxes { get; private set; }

    /// <summary>Maximum acceleration for a printing move, mm per step clock squared (M204 P)</summary>
    public float MaxPrintingAcceleration { get; private init; }

    /// <summary>Maximum acceleration for a travel move, mm per step clock squared (M204 T)</summary>
    public float MaxTravelAcceleration { get; private init; }

    /// <summary>Slowest a move may run, mm per step clock</summary>
    public float MinFeedrate { get; private init; }

    /// <summary>Axes the object model held when this snapshot was taken</summary>
    /// <remarks>
    /// Kept unclamped, unlike <see cref="NumAxes"/>, because it is what the object model is compared
    /// against rather than what can be planned for
    /// </remarks>
    private int ConfiguredAxes { get; init; }

    /// <summary>Extruders the object model held when this snapshot was taken</summary>
    private int ConfiguredExtruders { get; init; }

    /// <summary>
    /// First logical drive that is an extruder
    /// </summary>
    public int FirstExtruderDrive => NumDrives - NumExtruders;

    /// <summary>
    /// Whether this still describes the object model it was taken from
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <returns>True if the two agree about what the machine is made of</returns>
    /// <remarks>
    /// <para>
    /// Only M584 changes how many axes and extruders there are, and it calls
    /// <see cref="MovePlanner.ReconfigureAsync"/> after it has, so these agree in normal operation.
    /// They diverge when that reconfiguration did not happen or did not succeed - the engine rejected
    /// the description, or the motion service never started - and a planner working from a snapshot
    /// of a machine that no longer exists would address the wrong drives.
    /// </para>
    /// <para>
    /// Checked rather than clamped around, because there is no safe number of axes to plan for when
    /// the two disagree: the snapshot has the geometry and steps per mm for axes the object model may
    /// no longer have, and the object model has axes the snapshot knows nothing about
    /// </para>
    /// </remarks>
    public bool MatchesObjectModel(Move move)
        => move.Axes.Count == ConfiguredAxes && move.Extruders.Count == ConfiguredExtruders;

    /// <summary>
    /// Axes that can be addressed both here and in the object model
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <returns>The lower of the two counts</returns>
    /// <remarks>
    /// A bound for loops that read from both, so neither is indexed past its end. It is not a
    /// substitute for <see cref="MatchesObjectModel"/>: where the two disagree this silently plans
    /// for fewer axes than the machine has, which is why the move path refuses the move instead
    /// </remarks>
    public int SharedAxisCount(Move move) => Math.Min(NumAxes, move.Axes.Count);

    /// <summary>
    /// Extruders that can be addressed both here and in the object model
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <returns>The lower of the two counts</returns>
    /// <remarks>The extruder counterpart of <see cref="SharedAxisCount"/></remarks>
    public int SharedExtruderCount(Move move) => Math.Min(NumExtruders, move.Extruders.Count);

    /// <summary>
    /// Follow a change to one axis' travel limits
    /// </summary>
    /// <param name="axis">The axis whose limits changed</param>
    /// <param name="min">Its new lower limit in mm</param>
    /// <param name="max">Its new upper limit in mm</param>
    /// <remarks>
    /// The geometry holds the M208 box because every geometry limits positions with it, so it is a
    /// copy of <c>move.axes[].min</c> and <c>max</c> and has to follow them. M208 goes through
    /// <see cref="MovePlanner.ReconfigureAsync"/> and rebuilds the whole snapshot; G1 H3 writes the
    /// limit it measured and nothing else, so it updates the copy here instead of rebuilding a
    /// description that is otherwise unchanged
    /// </remarks>
    public void SetAxisLimits(int axis, float min, float max)
    {
        if (axis >= 0 && axis < NumAxes)
        {
            Geometry.SetAxisLimits(axis, min, max);
        }
    }

    /// <summary>
    /// The logical drive an extruder occupies
    /// </summary>
    /// <param name="extruder">Extruder number</param>
    /// <returns>Logical drive number</returns>
    public static int ExtruderToDrive(int extruder) => NumDrives - 1 - extruder;

    /// <summary>
    /// The axis occupying a logical drive, or -1
    /// </summary>
    /// <param name="drive">Logical drive number</param>
    /// <returns>Axis number</returns>
    /// <remarks>
    /// The axes come first and one apiece, so this is the identity below <see cref="NumAxes"/> - the
    /// same arrangement RepRapFirmware uses, where an axis' drive number is the axis number. A
    /// dual-motor axis is still one drive: its motors differ in which driver they are, not in what
    /// they move
    /// </remarks>
    public int DriveToAxis(int drive) => drive >= 0 && drive < NumAxes ? drive : -1;

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
    /// Give the geometry the travel limits M208 configured
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <param name="geometry">The machine's geometry</param>
    /// <remarks>
    /// <para>
    /// The geometry holds the M208 box because every geometry limits positions with it, so it is a
    /// copy of <c>move.axes[].min</c> and <c>max</c> and has to follow them.
    /// </para>
    /// <para>
    /// This is the one thing the object model still configures on the geometry rather than the other
    /// way round (§14.6 step 4c of <c>docs/devel/MCODE_MIGRATION.md</c>), and it is a call of its own
    /// rather than an assignment buried in <see cref="FromObjectModel"/> because it writes to the
    /// planner's geometry - taking a snapshot should not change the machine
    /// </para>
    /// </remarks>
    public static void ApplyAxisLimits(Move move, KinematicsEngine geometry)
    {
        int numAxes = Math.Min(move.Axes.Count, MotionLimits.MaxAxes);
        for (int axis = 0; axis < numAxes; axis++)
        {
            geometry.SetAxisLimits(axis, move.Axes[axis].Min, move.Axes[axis].Max);
        }
    }

    /// <summary>
    /// Take a snapshot of the object model's motion configuration
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <param name="geometry">
    /// The machine's geometry, which the planner owns rather than deriving from the object model
    /// </param>
    /// <returns>The snapshot, carrying both the planner's view of the machine and the engine's</returns>
    /// <remarks>
    /// <para>
    /// The caller must hold at least a read lock on the object model.
    /// </para>
    /// <para>
    /// Both derived forms are built here, in one walk of the axes and one of the extruders. They used
    /// to be built by two methods that each walked both collections, which meant a setting could be
    /// added to one walk and not the other and nothing would say so
    /// </para>
    /// </remarks>
    public static MotionParameters FromObjectModel(Move move, KinematicsEngine geometry)
    {
        int numAxes = Math.Min(move.Axes.Count, MotionLimits.MaxAxes);
        int numExtruders = Math.Min(move.Extruders.Count, MotionLimits.MaxExtruders);

        // Axes count up from zero and extruders down from the top, so more of them than the drive
        // space holds would make a drive both at once
        if (numAxes + numExtruders > NumDrives)
        {
            numExtruders = Math.Max(0, NumDrives - numAxes);
        }

        // M204 is per motion system, which is where the object model keeps it. The planner is not
        // per motion system yet, so the first one sets the limits for all of them
        MotionSystem? motionSystem = move.MotionSystems.Count > 0 ? move.MotionSystems[0] : null;
        MoveQueueItem? queue = move.Queue.Count > 0 ? move.Queue[0] : null;

        MachineConfig config = new()
        {
            NumTotalAxes = (byte)numAxes,
            NumExtruders = (byte)numExtruders,
            NumRings = (byte)Math.Max(1, Math.Min(move.MotionSystems.Count, MotionLimits.MaxRings)),
            NumDdasPerRing = (ushort)(queue is not null && queue.Length > 0 ? queue.Length : DefaultDdasPerRing),
            GracePeriodMs = (uint)MathF.Round((queue?.GracePeriod ?? DefaultGracePeriodSec) * 1000.0f)
        };

        MotionParameters parameters = new()
        {
            JerkPolicy = (uint)move.JerkPolicy,
            BacklashCorrectionDistanceFactor = (uint)Math.Max(1, move.BacklashFactor),
            NumAxes = numAxes,
            NumExtruders = numExtruders,
            ConfiguredAxes = move.Axes.Count,
            ConfiguredExtruders = move.Extruders.Count,
            Geometry = geometry,
            Config = config,
            MaxPrintingAcceleration = MotionUnits.AccelerationFromMmPerSecSquared(motionSystem?.PrintingAcceleration ?? DefaultAcceleration),
            MaxTravelAcceleration = MotionUnits.AccelerationFromMmPerSecSquared(motionSystem?.TravelAcceleration ?? DefaultAcceleration),
            MinFeedrate = MotionUnits.SpeedFromMmPerSec(move.MinimumMovementSpeed)
        };

        uint linearAxes = 0, rotationalAxes = 0, continuousRotationAxes = 0;
        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis a = move.Axes[axis];

            if (a.Rotational)
            {
                rotationalAxes |= 1u << axis;
                if (a.ContinuousRotation)
                {
                    continuousRotationAxes |= 1u << axis;
                }
            }
            else
            {
                linearAxes |= 1u << axis;
            }

            parameters.StepsPerMm[axis] = a.StepsPerMm;
            parameters.MaxFeedrates[axis] = MotionUnits.SpeedFromMmPerMin(a.Speed);
            parameters.Accelerations[axis] = MotionUnits.AccelerationFromMmPerSecSquared(a.Acceleration);
            parameters.ReducedAccelerations[axis] =
                MotionUnits.AccelerationFromMmPerSecSquared(a.ReducedAcceleration > 0.0f ? a.ReducedAcceleration : a.Acceleration);

            // Jerk is an instantaneous speed change, so it converts like a speed. The planner's copy
            // is the ordinary one only, because what it is for is the acceleration cap that pressure
            // advance imposes; the engine's lookahead is what needs the printing jerk as well
            parameters.InstantDvs[axis] = MotionUnits.SpeedFromMmPerMin(a.Jerk);
            parameters.PrintingInstantDvs[axis] = MotionUnits.SpeedFromMmPerMin(a.PrintingJerk);
            parameters.BacklashSteps[axis] = (int)MathF.Round(a.Backlash * a.StepsPerMm);

            config.DriveStepsPerMm[axis] = a.StepsPerMm;
            config.ControllingDrives[axis] = geometry.GetControllingDrives(axis);

            DriverId[] drivers = new DriverId[a.Drivers.Count];
            parameters.DriversPerDrive[axis] = a.Drivers.Count;
            for (int i = 0; i < a.Drivers.Count; i++)
            {
                drivers[i] = ToNativeDriver(a.Drivers[i]);
                parameters.ClaimDriver(a.Drivers[i], axis, i, $"axis {a.Letter}");
            }
            config.AxisDrivers[axis] = AxisDriversConfig.WithDrivers(drivers);
        }

        parameters.LinearAxes = linearAxes;
        parameters.RotationalAxes = rotationalAxes;

        // Some geometries have an axis that goes round whether or not M208 said so - a polar bed and
        // a SCARA joint with more than a full circle of travel both do - so the geometry gets to add
        // to what the configuration declared, masked to the axes that exist
        config.ContinuousRotationAxes = (continuousRotationAxes | geometry.ContinuousRotationAxes)
                                        & ((1u << Math.Min(numAxes, MotionLimits.MaxAxes)) - 1);

        for (int extruder = 0; extruder < numExtruders; extruder++)
        {
            Extruder e = move.Extruders[extruder];
            int drive = ExtruderToDrive(extruder);

            parameters.StepsPerMm[drive] = e.StepsPerMm;
            parameters.MaxFeedrates[drive] = MotionUnits.SpeedFromMmPerMin(e.Speed);
            parameters.Accelerations[drive] = MotionUnits.AccelerationFromMmPerSecSquared(e.Acceleration);
            parameters.ReducedAccelerations[drive] = MotionUnits.AccelerationFromMmPerSecSquared(e.Acceleration);
            parameters.InstantDvs[drive] = MotionUnits.SpeedFromMmPerMin(e.Jerk);
            parameters.PressureAdvanceClocks[drive] = MotionUnits.ClocksFromSeconds(e.PressAdv.K0);

            parameters.PrintingInstantDvs[drive] = MotionUnits.SpeedFromMmPerMin(e.PrintingJerk);
            parameters.NonlinearExtrusions[extruder] = new Native.NonlinearExtrusion
            {
                A = e.Nonlinear.A,
                B = e.Nonlinear.B,
                Limit = e.Nonlinear.UpperLimit
            };

            config.DriveStepsPerMm[drive] = e.StepsPerMm;
            config.ExtruderDrivers[extruder] = e.Driver is not null ? ToNativeDriver(e.Driver) : DriverId.None;

            if (e.Driver is not null)
            {
                parameters.ClaimDriver(e.Driver, drive, 0, $"extruder {extruder}");
                parameters.DriversPerDrive[drive] = 1;
            }
        }

        return parameters;
    }

    /// <summary>
    /// Convert an object model driver id to the native one
    /// </summary>
    /// <param name="driver">The driver</param>
    /// <returns>The native driver id</returns>
    private static DriverId ToNativeDriver(DuetAPI.Utility.DriverId driver)
        => new((byte)driver.Board, (byte)driver.Port);
}
