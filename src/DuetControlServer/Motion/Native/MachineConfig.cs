using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DuetControlServer.Motion.Native;

/// <summary>
/// Limits this build shares with the native motion engine
/// </summary>
/// <remarks>
/// These mirror the constants in <c>DuetSbcInterface/src/Compat/RepRapFirmware.h</c> and
/// <c>Motion/MachineConfig.h</c>. They size the fixed arrays in <see cref="MachineConfig"/>, so the two
/// sides must agree on them or the struct is a different length on each side
/// </remarks>
internal static class MotionLimits
{
    /// <summary>Maximum number of movement axes</summary>
    public const int MaxAxes = 30;

    /// <summary>Maximum number of extruders</summary>
    public const int MaxExtruders = 20;

    /// <summary>Logical drives the native side indexes by; may be less than MaxAxes + MaxExtruders</summary>
    /// <remarks>
    /// Also the width of every drive bitmap: the engine takes drive sets as a <c>uint</c>, so this
    /// being 32 is what lets one name every drive. Anything bounding a shift into such a bitmap
    /// should say this rather than 32, so that the two cannot drift apart silently
    /// </remarks>
    public const int MaxAxesPlusExtruders = 32;

    /// <summary>Maximum number of drivers that can move a single axis</summary>
    /// <remarks>
    /// Also the width of a per-driver bitmap within one drive, which is how the endstop correction
    /// tracks which motors of a dual-motor axis have reached their own switch
    /// </remarks>
    public const int MaxDriversPerAxis = 8;

    /// <summary>Movement systems, i.e. DDA rings, the native side builds</summary>
    public const int MaxRings = 2;

    /// <summary>Smallest lookahead depth that makes a ring a ring</summary>
    public const int MinDdasPerRing = 3;

    /// <summary>Largest lookahead depth the native side will build</summary>
    public const int MaxDdasPerRing = 1000;

    /// <summary>The controller's step clock, in Hz. Must match the native <c>stepClockRate</c></summary>
    public const float StepClockRate = 48000000.0f / 64.0f;
}

/// <summary>
/// Managed mirror of the native <c>DriverId</c>: one driver on one board
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 2)]
internal struct DriverId
{
    /// <summary>Driver number on the board named by <see cref="BoardAddress"/></summary>
    public byte LocalDriver;

    /// <summary>CAN address of the board carrying this driver</summary>
    public byte BoardAddress;

    /// <summary>
    /// Address meaning "no board", which is what an unconfigured driver holds. The native side reads
    /// it as not remote and drops the movement rather than addressing it to board zero
    /// </summary>
    public const byte NoCanAddress = 255;

    /// <summary>
    /// Create a driver id
    /// </summary>
    /// <param name="boardAddress">CAN address of the board</param>
    /// <param name="localDriver">Driver number on that board</param>
    public DriverId(byte boardAddress, byte localDriver)
    {
        BoardAddress = boardAddress;
        LocalDriver = localDriver;
    }

    /// <summary>An unconfigured driver</summary>
    public static DriverId None => new() { LocalDriver = 0, BoardAddress = NoCanAddress };
}

/// <summary>
/// The drivers that move one axis
/// </summary>
/// <remarks>
/// An axis with several drivers - a Z axis with three leadscrews, say - moves all of them together
/// </remarks>
/// <summary>
/// The drivers of one axis, inline
/// </summary>
[InlineArray(MotionLimits.MaxDriversPerAxis)]
internal struct DriverIdsPerAxis
{
    private DriverId _element0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 1 + (2 * MotionLimits.MaxDriversPerAxis))]
internal struct AxisDriversConfig
{
    /// <summary>Number of entries in <see cref="DriverNumbers"/> that are in use</summary>
    public byte NumDrivers;

    /// <summary>The drivers themselves</summary>
    public DriverIdsPerAxis DriverNumbers;

    /// <summary>An axis with no drivers assigned</summary>
    /// <remarks>
    /// Note that this is not <c>new AxisDriversConfig()</c>. A default <see cref="DriverId"/> in C# is
    /// all zero, and board address 0 is the main board rather than "no board", so an array left at its
    /// default would address every unconfigured driver to a real board. The native struct avoids this
    /// with a member initialiser, which does not survive being memcpyed over
    /// </remarks>
    public static AxisDriversConfig Empty => WithDrivers();

    /// <summary>
    /// An axis moved by the given drivers, with the remaining slots left unassigned
    /// </summary>
    /// <param name="drivers">The drivers that move this axis</param>
    /// <returns>The configuration</returns>
    /// <exception cref="ArgumentException">More drivers than an axis can have</exception>
    public static AxisDriversConfig WithDrivers(params DriverId[] drivers)
    {
        if (drivers.Length > MotionLimits.MaxDriversPerAxis)
        {
            throw new ArgumentException($"An axis may have at most {MotionLimits.MaxDriversPerAxis} drivers, got {drivers.Length}");
        }

        AxisDriversConfig result = new() { NumDrivers = (byte)drivers.Length };
        for (int i = 0; i < MotionLimits.MaxDriversPerAxis; i++)
        {
            result.DriverNumbers[i] = i < drivers.Length ? drivers[i] : DriverId.None;
        }
        return result;
    }
}

/// <summary>
/// M592 nonlinear extrusion coefficients for one extruder
/// </summary>
/// <remarks>
/// The commanded extrusion is scaled by <c>1 + min((A + B*v) * v, Limit)</c>, where <c>v</c> is the
/// average extrusion speed of the move in mm/sec
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct NonlinearExtrusion
{
    /// <summary>A coefficient</summary>
    public float A;

    /// <summary>B coefficient</summary>
    public float B;

    /// <summary>Largest correction this may apply, as a fraction of the commanded extrusion</summary>
    public float Limit;

    /// <summary>
    /// An extruder with no correction configured
    /// </summary>
    /// <remarks>
    /// Not <c>new NonlinearExtrusion()</c>: <see cref="Limit"/> defaults to RepRapFirmware's 0.2 rather
    /// than to zero, and a default-constructed array would silently clamp every correction to nothing
    /// </remarks>
    public static NonlinearExtrusion None => new() { A = 0.0F, B = 0.0F, Limit = DefaultLimit };

    /// <summary>RepRapFirmware's <c>DefaultNonlinearExtrusionLimit</c></summary>
    public const float DefaultLimit = 0.2F;
}

/// <summary>One float per logical drive</summary>
[InlineArray(MotionLimits.MaxAxesPlusExtruders)]
internal struct FloatPerDrive
{
    private float _element0;
}

/// <summary>One axis' driver configuration per axis</summary>
[InlineArray(MotionLimits.MaxAxes)]
internal struct AxisDriversPerAxis
{
    private AxisDriversConfig _element0;
}

/// <summary>One driver per extruder</summary>
[InlineArray(MotionLimits.MaxExtruders)]
internal struct DriverIdPerExtruder
{
    private DriverId _element0;
}

/// <summary>One bitmap per axis</summary>
[InlineArray(MotionLimits.MaxAxes)]
internal struct UIntPerAxis
{
    private uint _element0;
}

/// <summary>
/// Managed mirror of <c>DuetSbcInterface/src/Motion/MachineConfig.h</c>: the machine description the
/// native motion engine needs
/// </summary>
/// <remarks>
/// <para>
/// This is the machine itself: how many drives there are, what a microstep of each is worth, which
/// board drives it, and what the kinematics says about it. It describes moves that are already
/// queued, so replacing it is only safe at standstill.
/// </para>
/// <para>
/// The settings that can change mid-print are not here - jerk limits, pressure advance, backlash,
/// nonlinear extrusion and input shaping travel on each move instead, so that changing one cannot
/// reach a move that is already queued. See <c>docs/devel/MOTION_CONFIG_ORDERING.md</c>.
/// </para>
/// <para>
/// Two entries are kinematics results rather than configuration in the firmware's sense:
/// <see cref="ContinuousRotationAxes"/> and <see cref="ControllingDrives"/>. The native
/// <c>DDA::Prepare</c> needs to know whether an axis can take a short cut across 180 degrees and
/// which other motors must be energised to hold an axis in place on a CoreXY-like machine. In the
/// firmware it asks the <c>Kinematics</c> object; that object lives here now, so this side evaluates
/// both whenever the kinematics change and sends the answers down.
/// </para>
/// <para>
/// Everything is in the firmware's internal units - mm and step clocks - not the user-facing ones, so
/// the conversion happens here rather than on the motion path.
/// </para>
/// <para>
/// The native side clamps every count it reads to what its arrays can address, so a configuration
/// that is out of range is corrected rather than trusted. <see cref="Serialize"/> still produces the
/// values as given: what comes back from <c>GetConfig</c> natively is the authority on what was used.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MachineConfig
{
    // --- Machine shape ------------------------------------------------------------------------

    /// <summary>Axes in total, including any that exist only in the kinematics</summary>
    public byte NumTotalAxes;

    /// <summary>Number of extruders</summary>
    public byte NumExtruders;

    /// <summary>Movement systems: 1, or 2 for a second asynchronous one</summary>
    public byte NumRings;

    /// <summary>Declared on the native side so that this can reproduce it</summary>
    public byte Padding0;

    /// <summary>Lookahead depth, i.e. how many moves a ring holds</summary>
    public ushort NumDdasPerRing;

    /// <summary>Declared on the native side so that this can reproduce it</summary>
    public ushort Padding;

    /// <summary>How long to let moves accumulate before starting one, in milliseconds</summary>
    public uint GracePeriodMs;

    // --- Per-drive ----------------------------------------------------------------------------

    /// <summary>Microsteps per mm for each logical drive</summary>
    public FloatPerDrive DriveStepsPerMm;

    // --- Driver mapping -----------------------------------------------------------------------

    /// <summary>Which drivers move each axis</summary>
    public AxisDriversPerAxis AxisDrivers;

    /// <summary>Which driver drives each extruder</summary>
    public DriverIdPerExtruder ExtruderDrivers;

    /// <summary>Declared on the native side so that this can reproduce it</summary>
    public ushort Padding2;

    // --- Kinematics results ---------------------------------------------------------------------

    /// <summary>Axes that wrap at 360 degrees, so a move may take the short way round, as a bitmap</summary>
    public uint ContinuousRotationAxes;

    /// <summary>
    /// For each axis, the other drives that must be energised to hold it, as a bitmap. Empty on a
    /// Cartesian machine; on CoreXY, moving X requires both motors to be enabled
    /// </summary>
    public UIntPerAxis ControllingDrives;

    /// <summary>First logical drive that is an extruder</summary>
    public readonly int FirstExtruderDrive => MotionLimits.MaxAxesPlusExtruders - NumExtruders;

    /// <summary>
    /// A description of a machine that has not been configured
    /// </summary>
    /// <remarks>
    /// Not <c>new MachineConfig()</c>, which is all zeros. A zeroed <see cref="DriverId"/> has board
    /// address 0, and 0 is the main board rather than "no board", so every unconfigured driver would
    /// be addressed to a real one. The native struct avoids this with member initialisers, which do
    /// not survive being memcpyed over
    /// </remarks>
    public static MachineConfig Unconfigured()
    {
        MachineConfig config = new()
        {
            NumRings = 1,
            NumDdasPerRing = 40,
            GracePeriodMs = 10
        };
        for (int axis = 0; axis < MotionLimits.MaxAxes; axis++)
        {
            config.AxisDrivers[axis] = AxisDriversConfig.Empty;
        }
        for (int extruder = 0; extruder < MotionLimits.MaxExtruders; extruder++)
        {
            config.ExtruderDrivers[extruder] = DriverId.None;
        }
        return config;
    }

    /// <summary>
    /// Size of the serialised form, which is the struct itself
    /// </summary>
    /// <remarks>
    /// There is nothing to keep in step with the fields: the struct is blittable and laid out to
    /// match the native one, so its own size is the answer. The native side asserts the same number
    /// </remarks>
    public static int SerializedLength => Unsafe.SizeOf<MachineConfig>();

    /// <summary>
    /// Copy this configuration into <paramref name="destination"/>
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="SerializedLength"/> bytes</param>
    /// <returns>Number of bytes written</returns>
    /// <exception cref="ArgumentException">The buffer is too small</exception>
    /// <remarks>
    /// The bytes are the struct's own. This used to be written field by field against a hand-counted
    /// length, on the grounds that the native struct is not packed and reproducing its padding meant
    /// guessing at it - but the native side declares every padding byte it has, so there is nothing
    /// left to guess and nothing left to keep in step. <c>MachineConfigLayout</c> asserts the offsets
    /// against the numbers the native side asserts
    /// </remarks>
    public readonly int Serialize(Span<byte> destination)
    {
        if (destination.Length < SerializedLength)
        {
            throw new ArgumentException($"Need {SerializedLength} bytes for a MachineConfig, got {destination.Length}");
        }
        MemoryMarshal.Write(destination, in this);
        return SerializedLength;
    }
}
