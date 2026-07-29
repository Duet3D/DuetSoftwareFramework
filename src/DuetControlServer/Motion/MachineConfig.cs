using System;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// One configured axis
/// </summary>
internal sealed class AxisConfig
{
    /// <summary>Axis letter as the user refers to it, e.g. 'X'</summary>
    public char Letter { get; init; } = '?';

    /// <summary>Microsteps per mm (M92)</summary>
    public float StepsPerMm { get; set; } = 80.0f;

    /// <summary>Maximum speed in mm/sec (M203)</summary>
    public float MaxFeedrateMmPerSec { get; set; } = 100.0f;

    /// <summary>Maximum acceleration in mm/sec^2 (M201)</summary>
    public float AccelerationMmPerSecSquared { get; set; } = 1000.0f;

    /// <summary>Instantaneous speed change allowed at a junction, mm/sec (M566)</summary>
    public float JerkMmPerSec { get; set; } = 15.0f;

    /// <summary>Lower travel limit in mm (M208)</summary>
    public float MinPosition { get; set; }

    /// <summary>Upper travel limit in mm (M208)</summary>
    public float MaxPosition { get; set; } = 200.0f;

    /// <summary>Whether this axis rotates rather than translates, so its units are degrees</summary>
    public bool IsRotational { get; set; }

    /// <summary>Whether the axis wraps at 360 degrees, so a move may take the short way round</summary>
    public bool IsContinuousRotation { get; set; }

    /// <summary>Backlash to take up when the axis reverses, in mm (M425)</summary>
    public float BacklashMm { get; set; }

    /// <summary>The drivers that move this axis (M584)</summary>
    public DriverId[] Drivers { get; set; } = [];
}

/// <summary>
/// One configured extruder
/// </summary>
internal sealed class ExtruderConfig
{
    /// <summary>Microsteps per mm of filament (M92)</summary>
    public float StepsPerMm { get; set; } = 400.0f;

    /// <summary>Maximum speed in mm/sec (M203)</summary>
    public float MaxFeedrateMmPerSec { get; set; } = 60.0f;

    /// <summary>Maximum acceleration in mm/sec^2 (M201)</summary>
    public float AccelerationMmPerSecSquared { get; set; } = 2000.0f;

    /// <summary>Instantaneous speed change allowed at a junction, mm/sec (M566)</summary>
    public float JerkMmPerSec { get; set; } = 2.0f;

    /// <summary>Pressure advance time constant in seconds (M572)</summary>
    public float PressureAdvanceSeconds { get; set; }

    /// <summary>The driver that moves this extruder (M584)</summary>
    public DriverId Driver { get; set; } = DriverId.None;
}

/// <summary>
/// The machine as this side understands it: what the G-code path plans against, and the source the
/// native engine's <see cref="MotionConfig"/> is built from
/// </summary>
/// <remarks>
/// <para>
/// This is in user-facing units - mm, mm/sec, mm/sec^2 - because it is what the M-codes that
/// configure it are expressed in. <see cref="ToMotionConfig"/> does the one conversion into the
/// firmware's internal units, so it happens once here rather than on the motion path.
/// </para>
/// <para>
/// Logical drive numbering follows the native side: axes count up from 0 and extruders count down
/// from the top of the drive space. Both sides index the endpoint and direction vectors that way, so
/// it is not an internal detail
/// </para>
/// </remarks>
internal sealed class MachineConfig
{
    /// <summary>Axes the user can refer to</summary>
    public AxisConfig[] Axes { get; private set; } = [];

    /// <summary>Configured extruders</summary>
    public ExtruderConfig[] Extruders { get; private set; } = [];

    /// <summary>The machine geometry</summary>
    public KinematicsEngine Geometry { get; private set; } = CoreKinematicsEngine.TryCreate("cartesian")!;

    /// <summary>Lookahead depth, i.e. how many moves a ring holds</summary>
    public ushort NumDdasPerRing { get; set; } = 40;

    /// <summary>How long to let moves accumulate before starting one, in milliseconds</summary>
    public uint GracePeriodMs { get; set; } = 10;

    /// <summary>M566 P parameter: how aggressively moves may be melded</summary>
    public uint JerkPolicy { get; set; }

    /// <summary>Maximum acceleration for a printing move, mm/sec^2 (M204 P)</summary>
    public float MaxPrintingAccelerationMmPerSecSquared { get; set; } = 10000.0f;

    /// <summary>Maximum acceleration for a travel move, mm/sec^2 (M204 T)</summary>
    public float MaxTravelAccelerationMmPerSecSquared { get; set; } = 10000.0f;

    /// <summary>Slowest a move is allowed to run, mm/sec. Guards against overflow in the planner</summary>
    public float MinFeedrateMmPerSec { get; set; } = 0.5f;

    /// <summary>How far to spread a backlash correction, as a multiple of the backlash</summary>
    public uint BacklashCorrectionDistanceFactor { get; set; } = 10;

    /// <summary>Total number of axes, visible and otherwise</summary>
    public int NumAxes => Axes.Length;

    /// <summary>Number of extruders</summary>
    public int NumExtruders => Extruders.Length;

    /// <summary>
    /// First logical drive that is an extruder
    /// </summary>
    public int FirstExtruderDrive => MotionLimits.MaxAxesPlusExtruders - Extruders.Length;

    /// <summary>
    /// The logical drive an extruder occupies
    /// </summary>
    /// <param name="extruder">Extruder number</param>
    /// <returns>Logical drive number</returns>
    public int ExtruderToDrive(int extruder) => MotionLimits.MaxAxesPlusExtruders - 1 - extruder;

    /// <summary>Axes that translate rather than rotate, as a bitmap</summary>
    public uint LinearAxes { get; private set; }

    /// <summary>Axes that rotate, as a bitmap</summary>
    public uint RotationalAxes { get; private set; }

    /// <summary>Axes that wrap at 360 degrees, as a bitmap</summary>
    public uint ContinuousRotationAxes { get; private set; }

    /// <summary>
    /// Replace the axes and extruders, recomputing what is derived from them
    /// </summary>
    /// <param name="axes">The axes, in logical drive order</param>
    /// <param name="extruders">The extruders</param>
    /// <param name="geometry">The machine geometry</param>
    /// <exception cref="ArgumentException">More axes or extruders than the drive space holds</exception>
    public void Configure(AxisConfig[] axes, ExtruderConfig[] extruders, KinematicsEngine geometry)
    {
        if (axes.Length > MotionLimits.MaxAxes)
        {
            throw new ArgumentException($"At most {MotionLimits.MaxAxes} axes are supported, got {axes.Length}");
        }
        if (extruders.Length > MotionLimits.MaxExtruders)
        {
            throw new ArgumentException($"At most {MotionLimits.MaxExtruders} extruders are supported, got {extruders.Length}");
        }
        if (axes.Length + extruders.Length > MotionLimits.MaxAxesPlusExtruders)
        {
            // Axes count up from 0 and extruders down from the top, so overlapping them would make a
            // drive both an axis and an extruder
            throw new ArgumentException(
                $"{axes.Length} axes and {extruders.Length} extruders do not fit in {MotionLimits.MaxAxesPlusExtruders} logical drives");
        }

        Axes = axes;
        Extruders = extruders;
        Geometry = geometry;

        LinearAxes = RotationalAxes = ContinuousRotationAxes = 0;
        for (int axis = 0; axis < axes.Length; axis++)
        {
            uint bit = 1u << axis;
            if (axes[axis].IsRotational)
            {
                RotationalAxes |= bit;
                if (axes[axis].IsContinuousRotation)
                {
                    ContinuousRotationAxes |= bit;
                }
            }
            else
            {
                LinearAxes |= bit;
            }
        }
    }

    /// <summary>
    /// Index of the axis with the given letter, or -1
    /// </summary>
    /// <param name="letter">Axis letter, case-insensitive</param>
    /// <returns>Axis index</returns>
    public int FindAxis(char letter)
    {
        char upper = char.ToUpperInvariant(letter);
        for (int axis = 0; axis < Axes.Length; axis++)
        {
            if (char.ToUpperInvariant(Axes[axis].Letter) == upper)
            {
                return axis;
            }
        }
        return -1;
    }

    /// <summary>
    /// Per-drive microsteps per mm, indexed by logical drive
    /// </summary>
    /// <returns>The array</returns>
    public float[] BuildStepsPerMm()
    {
        float[] stepsPerMm = new float[MotionLimits.MaxAxesPlusExtruders];

        // Never zero: this divides in MotorStepsToCartesian, and a drive that is not configured must
        // not turn a position into an infinity
        Array.Fill(stepsPerMm, 1.0f);

        for (int axis = 0; axis < Axes.Length; axis++)
        {
            stepsPerMm[axis] = Axes[axis].StepsPerMm;
        }
        for (int extruder = 0; extruder < Extruders.Length; extruder++)
        {
            stepsPerMm[ExtruderToDrive(extruder)] = Extruders[extruder].StepsPerMm;
        }
        return stepsPerMm;
    }

    /// <summary>
    /// Per-drive maximum speed in mm per step clock, indexed by logical drive
    /// </summary>
    /// <returns>The array</returns>
    public float[] BuildMaxFeedrates()
    {
        float[] feedrates = new float[MotionLimits.MaxAxesPlusExtruders];
        for (int axis = 0; axis < Axes.Length; axis++)
        {
            feedrates[axis] = Axes[axis].MaxFeedrateMmPerSec / MotionLimits.StepClockRate;
        }
        for (int extruder = 0; extruder < Extruders.Length; extruder++)
        {
            feedrates[ExtruderToDrive(extruder)] = Extruders[extruder].MaxFeedrateMmPerSec / MotionLimits.StepClockRate;
        }
        return feedrates;
    }

    /// <summary>
    /// Per-drive maximum acceleration in mm per step clock squared, indexed by logical drive
    /// </summary>
    /// <returns>The array</returns>
    public float[] BuildAccelerations()
    {
        float[] accelerations = new float[MotionLimits.MaxAxesPlusExtruders];
        float clockSquared = MotionLimits.StepClockRate * MotionLimits.StepClockRate;
        for (int axis = 0; axis < Axes.Length; axis++)
        {
            accelerations[axis] = Axes[axis].AccelerationMmPerSecSquared / clockSquared;
        }
        for (int extruder = 0; extruder < Extruders.Length; extruder++)
        {
            accelerations[ExtruderToDrive(extruder)] = Extruders[extruder].AccelerationMmPerSecSquared / clockSquared;
        }
        return accelerations;
    }

    /// <summary>
    /// Build the description to push down to the native motion engine
    /// </summary>
    /// <returns>The native configuration</returns>
    /// <remarks>
    /// This is where user units become the firmware's internal ones. It also evaluates the two
    /// kinematics answers the native planner needs but cannot work out for itself, because the
    /// geometry lives on this side
    /// </remarks>
    public MotionConfig ToMotionConfig()
    {
        MotionConfig config = new()
        {
            NumVisibleAxes = (byte)Axes.Length,
            NumTotalAxes = (byte)Axes.Length,
            NumExtruders = (byte)Extruders.Length,
            NumRings = 1,
            NumDdasPerRing = NumDdasPerRing,
            GracePeriodMs = GracePeriodMs,
            JerkPolicy = JerkPolicy,
            BacklashCorrectionDistanceFactor = BacklashCorrectionDistanceFactor,
            ContinuousRotationAxes = ContinuousRotationAxes
        };

        for (int axis = 0; axis < Axes.Length; axis++)
        {
            AxisConfig a = Axes[axis];
            config.DriveStepsPerMm[axis] = a.StepsPerMm;

            // Jerk is an instantaneous speed change, so it converts like a speed
            float jerk = a.JerkMmPerSec / MotionLimits.StepClockRate;
            config.InstantDvs[axis] = jerk;
            config.PrintingInstantDvs[axis] = jerk;

            config.BacklashSteps[axis] = (int)MathF.Round(a.BacklashMm * a.StepsPerMm);
            config.AxisDrivers[axis] = AxisDriversConfig.WithDrivers(a.Drivers);
            config.ControllingDrives[axis] = Geometry.GetControllingDrives(axis);
        }

        for (int extruder = 0; extruder < Extruders.Length; extruder++)
        {
            ExtruderConfig e = Extruders[extruder];
            int drive = ExtruderToDrive(extruder);

            config.DriveStepsPerMm[drive] = e.StepsPerMm;
            float jerk = e.JerkMmPerSec / MotionLimits.StepClockRate;
            config.InstantDvs[drive] = jerk;
            config.PrintingInstantDvs[drive] = jerk;

            // Pressure advance is a time, so it converts to step clocks rather than dividing by them
            config.PressureAdvanceClocks[drive] = e.PressureAdvanceSeconds * MotionLimits.StepClockRate;
            config.ExtruderDrivers[extruder] = e.Driver;
        }

        return config;
    }

    /// <summary>
    /// A three-axis Cartesian machine with one extruder, one driver per axis on board 0
    /// </summary>
    /// <returns>The configuration</returns>
    /// <remarks>
    /// What the machine looks like before config.g has been read. Real configuration replaces this
    /// wholesale rather than adjusting it
    /// </remarks>
    public static MachineConfig CreateDefault()
    {
        MachineConfig config = new();
        AxisConfig[] axes =
        [
            new() { Letter = 'X', StepsPerMm = 80.0f, MaxPosition = 200.0f, Drivers = [new DriverId(0, 0)] },
            new() { Letter = 'Y', StepsPerMm = 80.0f, MaxPosition = 200.0f, Drivers = [new DriverId(0, 1)] },
            new() { Letter = 'Z', StepsPerMm = 400.0f, MaxPosition = 200.0f, MaxFeedrateMmPerSec = 10.0f, JerkMmPerSec = 0.5f, Drivers = [new DriverId(0, 2)] }
        ];
        ExtruderConfig[] extruders = [new() { Driver = new DriverId(0, 3) }];

        config.Configure(axes, extruders, CoreKinematicsEngine.TryCreate("cartesian")!);
        return config;
    }
}
