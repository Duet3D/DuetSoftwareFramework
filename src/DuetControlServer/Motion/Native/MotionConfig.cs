using System;
using System.Runtime.InteropServices;

namespace DuetControlServer.Motion.Native;

/// <summary>
/// Limits this build shares with the native motion engine
/// </summary>
/// <remarks>
/// These mirror the constants in <c>DuetSbcInterface/src/Compat/RepRapFirmware.h</c> and
/// <c>Motion/MotionConfig.h</c>. They size the fixed arrays in <see cref="MotionConfig"/>, so the two
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
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 1 + (2 * MotionLimits.MaxDriversPerAxis))]
internal struct AxisDriversConfig
{
    /// <summary>Number of entries in <see cref="DriverNumbers"/> that are in use</summary>
    public byte NumDrivers;

    /// <summary>The drivers themselves</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MotionLimits.MaxDriversPerAxis)]
    public DriverId[] DriverNumbers;

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

        DriverId[] driverNumbers = new DriverId[MotionLimits.MaxDriversPerAxis];
        Array.Fill(driverNumbers, DriverId.None);
        drivers.CopyTo(driverNumbers, 0);
        return new AxisDriversConfig { NumDrivers = (byte)drivers.Length, DriverNumbers = driverNumbers };
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

/// <summary>
/// Managed mirror of <c>DuetSbcInterface/src/Motion/MotionConfig.h</c>: the machine description the
/// native motion engine needs
/// </summary>
/// <remarks>
/// <para>
/// This side owns configuration. It parses M92, M201, M203, M566, M425, M569 and M584, and it owns
/// the kinematics; this is the subset of the result that the native planner actually reads while
/// planning and preparing moves.
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
internal sealed class MotionConfig
{
    // --- Machine shape ------------------------------------------------------------------------

    /// <summary>Axes the user can refer to</summary>
    public byte NumVisibleAxes { get; set; }

    /// <summary>Axes in total, including any that exist only in the kinematics</summary>
    public byte NumTotalAxes { get; set; }

    /// <summary>Number of extruders</summary>
    public byte NumExtruders { get; set; }

    /// <summary>Movement systems: 1, or 2 for a second asynchronous one</summary>
    public byte NumRings { get; set; } = 1;

    /// <summary>Lookahead depth, i.e. how many moves a ring holds</summary>
    public ushort NumDdasPerRing { get; set; } = 40;

    /// <summary>How long to let moves accumulate before starting one, in milliseconds</summary>
    public uint GracePeriodMs { get; set; } = 10;

    // --- Per-drive limits ---------------------------------------------------------------------

    /// <summary>Microsteps per mm for each logical drive</summary>
    public float[] DriveStepsPerMm { get; } = new float[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>
    /// Instantaneous speed change a drive tolerates at a junction between moves, in mm per step clock
    /// </summary>
    public float[] InstantDvs { get; } = new float[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>
    /// The same, for a junction between two extruding moves, where a lower limit avoids visible
    /// artefacts
    /// </summary>
    public float[] PrintingInstantDvs { get; } = new float[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>
    /// Pressure advance time constant per drive, in step clocks. Zero for anything that is not an
    /// extruder
    /// </summary>
    public float[] PressureAdvanceClocks { get; } = new float[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>Backlash to take up when a drive reverses, in microsteps</summary>
    public int[] BacklashSteps { get; } = new int[MotionLimits.MaxAxes];

    /// <summary>
    /// How far to spread the backlash correction over, as a multiple of the backlash itself
    /// </summary>
    public uint BacklashCorrectionDistanceFactor { get; set; } = 10;

    // --- Junction policy ----------------------------------------------------------------------

    /// <summary>
    /// M566 P parameter. 0 allows a junction speed only between moves of the same kind; higher values
    /// allow melding more aggressively
    /// </summary>
    public uint JerkPolicy { get; set; }

    // --- Driver mapping -----------------------------------------------------------------------

    /// <summary>Which drivers move each axis</summary>
    public AxisDriversConfig[] AxisDrivers { get; } = CreateAxisDrivers();

    /// <summary>Which driver drives each extruder</summary>
    public DriverId[] ExtruderDrivers { get; } = CreateExtruderDrivers();

    // --- Kinematics results ---------------------------------------------------------------------

    /// <summary>Axes that wrap at 360 degrees, so a move may take the short way round, as a bitmap</summary>
    public uint ContinuousRotationAxes { get; set; }

    /// <summary>
    /// For each axis, the other drives that must be energised to hold it, as a bitmap. Empty on a
    /// Cartesian machine; on CoreXY, moving X requires both motors to be enabled
    /// </summary>
    public uint[] ControllingDrives { get; } = new uint[MotionLimits.MaxAxes];

    // --- Input shaping --------------------------------------------------------------------------

    /// <summary>
    /// How long the expansion boards' input shaper spreads a move over, in step clocks
    /// </summary>
    /// <remarks>
    /// Nothing on either side shapes anything: shaping happens on the boards. But the boards' motion
    /// is the shaped profile while the segments built natively are the unshaped one, so during
    /// acceleration the tracked position leads the real one by up to this long. Endpoints still agree
    /// exactly. Zero until shaping is enabled on the boards
    /// </remarks>
    public uint ShapingTimeClocks { get; set; }

    // --- Extrusion correction -------------------------------------------------------------------

    /// <summary>
    /// M592 nonlinear extrusion coefficients, per extruder
    /// </summary>
    public NonlinearExtrusion[] NonlinearExtrusions { get; } = CreateNonlinearExtrusions();

    // --- Derived ----------------------------------------------------------------------------------

    /// <summary>
    /// First logical drive that is an extruder. Extruders count down from the top of the drive space,
    /// which is how the native side packs axes and extruders into one index range
    /// </summary>
    public int FirstExtruderDrive => MotionLimits.MaxAxesPlusExtruders - NumExtruders;

    /// <summary>
    /// The logical drive number of an extruder
    /// </summary>
    /// <param name="extruder">Extruder number</param>
    /// <returns>Logical drive number</returns>
    public static int ExtruderToLogicalDrive(int extruder) => MotionLimits.MaxAxesPlusExtruders - 1 - extruder;

    /// <summary>
    /// Total size of the serialised struct
    /// </summary>
    /// <remarks>
    /// Computed from the layout below rather than from <c>Marshal.SizeOf</c>, because the struct is
    /// written field by field: this is the number the native side asserts against
    /// </remarks>
    public const int SerializedLength =
        1 + 1 + 1                                                    // numVisibleAxes, numTotalAxes, numExtruders
        + 1 + 2 + 2                                                  // numRings, numDdasPerRing, padding
        + 4                                                          // gracePeriodMs
        + (4 * MotionLimits.MaxAxesPlusExtruders)                    // driveStepsPerMm
        + (4 * MotionLimits.MaxAxesPlusExtruders)                    // instantDvs
        + (4 * MotionLimits.MaxAxesPlusExtruders)                    // printingInstantDvs
        + (4 * MotionLimits.MaxAxesPlusExtruders)                    // pressureAdvanceClocks
        + (4 * MotionLimits.MaxAxes)                                 // backlashSteps
        + 4                                                          // backlashCorrectionDistanceFactor
        + 4                                                          // jerkPolicy
        + ((1 + (2 * MotionLimits.MaxDriversPerAxis)) * MotionLimits.MaxAxes)    // axisDrivers
        + (2 * MotionLimits.MaxExtruders)                            // extruderDrivers
        + 2                                                          // padding2
        + 4                                                          // continuousRotationAxes
        + (4 * MotionLimits.MaxAxes)                                 // controllingDrives
        + 4                                                          // shapingTimeClocks
        + (12 * MotionLimits.MaxExtruders);                          // nonlinearExtrusion

    /// <summary>
    /// Serialise this configuration into the byte layout the native side expects
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="SerializedLength"/> bytes</param>
    /// <returns>Number of bytes written</returns>
    /// <exception cref="ArgumentException">The buffer is too small</exception>
    /// <remarks>
    /// Written field by field rather than by marshalling a struct, because the native struct is not
    /// packed: the compiler is free to insert padding between members, and reproducing that from here
    /// would mean guessing at it. The native <c>CApi</c> reads the same sequence back
    /// </remarks>
    public int Serialize(Span<byte> destination)
    {
        if (destination.Length < SerializedLength)
        {
            throw new ArgumentException($"Need {SerializedLength} bytes for a MotionConfig, got {destination.Length}");
        }

        SpanWriter writer = new(destination);
        writer.WriteByte(NumVisibleAxes);
        writer.WriteByte(NumTotalAxes);
        writer.WriteByte(NumExtruders);
        writer.WriteByte(NumRings);
        writer.WriteUInt16(NumDdasPerRing);
        writer.WriteUInt16(0);          // padding, declared on the native side so this can match it
        writer.WriteUInt32(GracePeriodMs);

        writer.WriteSingles(DriveStepsPerMm);
        writer.WriteSingles(InstantDvs);
        writer.WriteSingles(PrintingInstantDvs);
        writer.WriteSingles(PressureAdvanceClocks);
        writer.WriteInt32s(BacklashSteps);
        writer.WriteUInt32(BacklashCorrectionDistanceFactor);
        writer.WriteUInt32(JerkPolicy);

        foreach (AxisDriversConfig axis in AxisDrivers)
        {
            writer.WriteByte(axis.NumDrivers);
            for (int i = 0; i < MotionLimits.MaxDriversPerAxis; i++)
            {
                DriverId driver = axis.DriverNumbers is not null && i < axis.DriverNumbers.Length ? axis.DriverNumbers[i] : DriverId.None;
                writer.WriteByte(driver.LocalDriver);
                writer.WriteByte(driver.BoardAddress);
            }
        }

        foreach (DriverId driver in ExtruderDrivers)
        {
            writer.WriteByte(driver.LocalDriver);
            writer.WriteByte(driver.BoardAddress);
        }

        writer.WriteUInt16(0);          // padding2, realigning the bitmaps that follow
        writer.WriteUInt32(ContinuousRotationAxes);
        writer.WriteUInt32s(ControllingDrives);
        writer.WriteUInt32(ShapingTimeClocks);

        foreach (NonlinearExtrusion nonlinear in NonlinearExtrusions)
        {
            writer.WriteSingle(nonlinear.A);
            writer.WriteSingle(nonlinear.B);
            writer.WriteSingle(nonlinear.Limit);
        }

        return writer.Position;
    }

    private static AxisDriversConfig[] CreateAxisDrivers()
    {
        AxisDriversConfig[] result = new AxisDriversConfig[MotionLimits.MaxAxes];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = AxisDriversConfig.Empty;
        }
        return result;
    }

    private static NonlinearExtrusion[] CreateNonlinearExtrusions()
    {
        NonlinearExtrusion[] result = new NonlinearExtrusion[MotionLimits.MaxExtruders];
        Array.Fill(result, NonlinearExtrusion.None);
        return result;
    }

    private static DriverId[] CreateExtruderDrivers()
    {
        DriverId[] result = new DriverId[MotionLimits.MaxExtruders];
        Array.Fill(result, DriverId.None);
        return result;
    }

    /// <summary>
    /// Sequential little-endian writer over a span
    /// </summary>
    /// <param name="destination">Buffer to write into</param>
    private ref struct SpanWriter(Span<byte> destination)
    {
        private readonly Span<byte> _destination = destination;

        /// <summary>Bytes written so far</summary>
        public int Position { get; private set; }

        public void WriteByte(byte value) => _destination[Position++] = value;

        public void WriteUInt16(ushort value)
        {
            BitConverter.TryWriteBytes(_destination[Position..], value);
            Position += sizeof(ushort);
        }

        public void WriteUInt32(uint value)
        {
            BitConverter.TryWriteBytes(_destination[Position..], value);
            Position += sizeof(uint);
        }

        public void WriteSingle(float value)
        {
            BitConverter.TryWriteBytes(_destination[Position..], value);
            Position += sizeof(float);
        }

        public void WriteSingles(ReadOnlySpan<float> values)
        {
            MemoryMarshal.AsBytes(values).CopyTo(_destination[Position..]);
            Position += values.Length * sizeof(float);
        }

        public void WriteInt32s(ReadOnlySpan<int> values)
        {
            MemoryMarshal.AsBytes(values).CopyTo(_destination[Position..]);
            Position += values.Length * sizeof(int);
        }

        public void WriteUInt32s(ReadOnlySpan<uint> values)
        {
            MemoryMarshal.AsBytes(values).CopyTo(_destination[Position..]);
            Position += values.Length * sizeof(uint);
        }
    }
}
