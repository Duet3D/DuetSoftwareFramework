using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using UnitTests.Utility;

namespace UnitTests.Motion;

/// <summary>
/// The serialised form of <see cref="MachineConfig"/> against the native struct it is copied into
/// </summary>
/// <remarks>
/// <c>DuetSbc_MotionConfigure</c> memcpys these bytes straight into a C++ <c>MachineConfig</c> and
/// refuses anything that is not exactly the right length, so a mismatch here is not a subtle bug: at
/// best the configuration is rejected and no move is ever scheduled, at worst every field after the
/// mismatch is read from the wrong offset. The numbers below are the ones
/// <c>tests/MachineConfigLayoutTests.cpp</c> asserts on the other side
/// </remarks>
[TestFixture]
public class MachineConfigLayout
{
    /// <summary>Offsets the native side asserts, so the two can be compared field by field</summary>
    private const int GracePeriodMsOffset = 8;
    private const int DriveStepsPerMmOffset = 12;
    private const int AxisDriversOffset = 140;
    private const int ExtruderDriversOffset = 650;
    private const int ContinuousRotationAxesOffset = 692;
    private const int ControllingDrivesOffset = 696;

    [Test]
    public void SerializedLengthMatchesTheNativeStruct()
    {
        // The struct's own size, so nothing is kept in step by hand. What this checks is that the
        // two sides still agree on the number - the native side asserts the same 816
        Assert.That(MachineConfig.SerializedLength, Is.EqualTo(816));
    }

    [Test]
    public void EveryFieldSitsAtTheNativeOffset()
    {
        // Serialize is a memcpy of the struct, so these offsets are the whole of the contract. The
        // numbers are the ones tests/MachineConfigLayoutTests.cpp asserts on the other side
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.NumTotalAxes)), Is.EqualTo((nint)0));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.NumExtruders)), Is.EqualTo((nint)1));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.NumRings)), Is.EqualTo((nint)2));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.NumDdasPerRing)), Is.EqualTo((nint)4));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.GracePeriodMs)), Is.EqualTo((nint)GracePeriodMsOffset));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.DriveStepsPerMm)), Is.EqualTo((nint)DriveStepsPerMmOffset));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.AxisDrivers)), Is.EqualTo((nint)AxisDriversOffset));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.ExtruderDrivers)), Is.EqualTo((nint)ExtruderDriversOffset));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.ContinuousRotationAxes)), Is.EqualTo((nint)ContinuousRotationAxesOffset));
            Assert.That(Marshal.OffsetOf<MachineConfig>(nameof(MachineConfig.ControllingDrives)), Is.EqualTo((nint)ControllingDrivesOffset));
        });
    }

    [Test]
    public void LimitsMatchTheNativeBuild()
    {
        Assert.That(MotionLimits.MaxAxes, Is.EqualTo(30));
        Assert.That(MotionLimits.MaxExtruders, Is.EqualTo(20));
        Assert.That(MotionLimits.MaxAxesPlusExtruders, Is.EqualTo(32));
        Assert.That(MotionLimits.MaxDriversPerAxis, Is.EqualTo(8));
    }

    [Test]
    public void AxisDriversConfigDeclaresTheStrideItIsWrittenAt()
    {
        // The struct is never marshalled - it holds a managed array, so nothing checks its declared
        // Size against anything, and Serialize writes each axis by hand. What that declaration has
        // to agree with is the distance between one axis and the next in the record, which is what
        // puts extruderDrivers at the offset the native side reads it from rather than 30 axes'
        // worth of some other number further along
        int stride = (ExtruderDriversOffset - AxisDriversOffset) / MotionLimits.MaxAxes;

        Assert.Multiple(() =>
        {
            Assert.That(stride, Is.EqualTo(typeof(AxisDriversConfig).StructLayoutAttribute!.Size), "sizeof(AxisDriversConfig)");
            Assert.That(stride, Is.EqualTo(1 + (MotionLimits.MaxDriversPerAxis * Marshal.SizeOf<DriverId>())), "numDrivers and a DriverId per driver");
            Assert.That(PackedStructSize.OfFields(typeof(DriverId)), Is.EqualTo(Marshal.SizeOf<DriverId>()), "the fields fill DriverId, leaving no padding");
        });
    }

    [Test]
    public void SerializeWritesEveryFieldAtTheNativeOffset()
    {
        MachineConfig config = MachineConfig.Unconfigured();
        config.NumTotalAxes = 4;
        config.NumExtruders = 2;
        config.NumRings = 1;
        config.NumDdasPerRing = 40;
        config.GracePeriodMs = 10;
        config.ContinuousRotationAxes = 0x0000_0020;
        config.DriveStepsPerMm[0] = 80.0f;
        config.DriveStepsPerMm[MotionLimits.MaxAxesPlusExtruders - 1] = 420.0f;
        config.ControllingDrives[1] = 0x3;

        config.AxisDrivers[0] = AxisDriversConfig.WithDrivers(new DriverId(1, 4), new DriverId(2, 5));
        config.ExtruderDrivers[0] = new DriverId(3, 6);

        byte[] buffer = new byte[MachineConfig.SerializedLength];
        int written = config.Serialize(buffer);

        Assert.That(written, Is.EqualTo(MachineConfig.SerializedLength));

        // Machine shape
        Assert.That(buffer[0], Is.EqualTo(4), "numTotalAxes");
        Assert.That(buffer[1], Is.EqualTo(2), "numExtruders");
        Assert.That(buffer[2], Is.EqualTo(1), "numRings");
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4)), Is.EqualTo(40));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(GracePeriodMsOffset)), Is.EqualTo(10));

        // Per-drive arrays
        Assert.That(BitConverter.ToSingle(buffer, DriveStepsPerMmOffset), Is.EqualTo(80.0f));
        Assert.That(BitConverter.ToSingle(buffer, DriveStepsPerMmOffset + ((MotionLimits.MaxAxesPlusExtruders - 1) * 4)), Is.EqualTo(420.0f));

        // Driver mapping. AxisDriversConfig is 1 + 8*2 bytes with no padding, which is what puts
        // extruderDrivers at 650 rather than somewhere the compiler chose
        Assert.That(buffer[AxisDriversOffset], Is.EqualTo(2), "numDrivers");
        Assert.That(buffer[AxisDriversOffset + 1], Is.EqualTo(4), "first driver, local number");
        Assert.That(buffer[AxisDriversOffset + 2], Is.EqualTo(1), "first driver, board address");
        Assert.That(buffer[AxisDriversOffset + 3], Is.EqualTo(5), "second driver, local number");
        Assert.That(buffer[AxisDriversOffset + 4], Is.EqualTo(2), "second driver, board address");
        Assert.That(buffer[ExtruderDriversOffset], Is.EqualTo(6), "extruder driver, local number");
        Assert.That(buffer[ExtruderDriversOffset + 1], Is.EqualTo(3), "extruder driver, board address");

        // Kinematics results, after the alignment gap that precedes them
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ContinuousRotationAxesOffset)), Is.EqualTo(0x0000_0020));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ControllingDrivesOffset + 4)), Is.EqualTo(0x3));
    }

    [Test]
    public void UnconfiguredDriversSerialiseAsNoBoard()
    {
        // Board address 0 is the main board, not "no board", so a driver left at its default would
        // be addressed to a real one. A zeroed struct says exactly that, which is why
        // MachineConfig.Unconfigured() exists and why nothing may use new MachineConfig() as a
        // starting point
        MachineConfig config = MachineConfig.Unconfigured();
        byte[] buffer = new byte[MachineConfig.SerializedLength];
        config.Serialize(buffer);

        Assert.That(buffer[AxisDriversOffset + 2], Is.EqualTo(DriverId.NoCanAddress));
        Assert.That(buffer[ExtruderDriversOffset + 1], Is.EqualTo(DriverId.NoCanAddress));
    }

    [Test]
    public void AZeroedConfigurationWouldAddressDriversToBoardZero()
    {
        // The reason the factory above is not optional, asserted so that anyone who makes the struct
        // zero-safe can delete both this and the factory rather than wondering why it is there
        MachineConfig zeroed = default;
        byte[] buffer = new byte[MachineConfig.SerializedLength];
        zeroed.Serialize(buffer);

        Assert.That(buffer[AxisDriversOffset + 2], Is.EqualTo(0), "a zeroed struct names board 0");
    }

    [Test]
    public void SerializeRejectsAShortBuffer()
    {
        MachineConfig config = new();
        byte[] tooSmall = new byte[MachineConfig.SerializedLength - 1];
        Assert.Throws<ArgumentException>(() => config.Serialize(tooSmall));
    }
}
