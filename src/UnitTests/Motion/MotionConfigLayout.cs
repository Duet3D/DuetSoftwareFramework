using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using UnitTests.Utility;

namespace UnitTests.Motion;

/// <summary>
/// The serialised form of <see cref="MotionConfig"/> against the native struct it is copied into
/// </summary>
/// <remarks>
/// <c>DuetSbc_MotionConfigure</c> memcpys these bytes straight into a C++ <c>MotionConfig</c> and
/// refuses anything that is not exactly the right length, so a mismatch here is not a subtle bug: at
/// best the configuration is rejected and no move is ever scheduled, at worst every field after the
/// mismatch is read from the wrong offset. The numbers below are the ones
/// <c>tests/MotionConfigLayoutTests.cpp</c> asserts on the other side
/// </remarks>
[TestFixture]
public class MotionConfigLayout
{
    /// <summary>Offsets the native side asserts, so the two can be compared field by field</summary>
    private const int GracePeriodMsOffset = 8;
    private const int DriveStepsPerMmOffset = 12;
    private const int InstantDvsOffset = 140;
    private const int BacklashStepsOffset = 524;
    private const int JerkPolicyOffset = 648;
    private const int AxisDriversOffset = 652;
    private const int ExtruderDriversOffset = 1162;
    private const int ContinuousRotationAxesOffset = 1204;
    private const int ControllingDrivesOffset = 1208;
    private const int ShapingTimeClocksOffset = 1328;

    [Test]
    public void SerializedLengthMatchesTheNativeStruct()
    {
        Assert.That(MotionConfig.SerializedLength, Is.EqualTo(1332));
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
        MotionConfig config = new()
        {
            NumVisibleAxes = 3,
            NumTotalAxes = 4,
            NumExtruders = 2,
            NumRings = 1,
            NumDdasPerRing = 40,
            GracePeriodMs = 10,
            BacklashCorrectionDistanceFactor = 7,
            JerkPolicy = 1,
            ContinuousRotationAxes = 0x0000_0020,
            ShapingTimeClocks = 1234
        };
        config.DriveStepsPerMm[0] = 80.0f;
        config.DriveStepsPerMm[MotionLimits.MaxAxesPlusExtruders - 1] = 420.0f;
        config.InstantDvs[0] = 0.25f;
        config.BacklashSteps[0] = -13;
        config.ControllingDrives[1] = 0x3;

        config.AxisDrivers[0] = AxisDriversConfig.WithDrivers(new DriverId(1, 4), new DriverId(2, 5));
        config.ExtruderDrivers[0] = new DriverId(3, 6);

        byte[] buffer = new byte[MotionConfig.SerializedLength];
        int written = config.Serialize(buffer);

        Assert.That(written, Is.EqualTo(MotionConfig.SerializedLength));

        // Machine shape, including the padding that only exists so both sides agree on the offsets
        Assert.That(buffer[0], Is.EqualTo(3));
        Assert.That(buffer[1], Is.EqualTo(4));
        Assert.That(buffer[2], Is.EqualTo(2));
        Assert.That(buffer[3], Is.EqualTo(1));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4)), Is.EqualTo(40));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6)), Is.EqualTo(0), "padding");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(GracePeriodMsOffset)), Is.EqualTo(10));

        // Per-drive arrays
        Assert.That(BitConverter.ToSingle(buffer, DriveStepsPerMmOffset), Is.EqualTo(80.0f));
        Assert.That(BitConverter.ToSingle(buffer, DriveStepsPerMmOffset + ((MotionLimits.MaxAxesPlusExtruders - 1) * 4)), Is.EqualTo(420.0f));
        Assert.That(BitConverter.ToSingle(buffer, InstantDvsOffset), Is.EqualTo(0.25f));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(BacklashStepsOffset)), Is.EqualTo(-13));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(JerkPolicyOffset)), Is.EqualTo(1));

        // Driver mapping. AxisDriversConfig is 1 + 8*2 bytes with no padding, which is what puts
        // extruderDrivers at 1162 rather than somewhere the compiler chose
        Assert.That(buffer[AxisDriversOffset], Is.EqualTo(2), "numDrivers");
        Assert.That(buffer[AxisDriversOffset + 1], Is.EqualTo(4), "first driver, local number");
        Assert.That(buffer[AxisDriversOffset + 2], Is.EqualTo(1), "first driver, board address");
        Assert.That(buffer[AxisDriversOffset + 3], Is.EqualTo(5), "second driver, local number");
        Assert.That(buffer[AxisDriversOffset + 4], Is.EqualTo(2), "second driver, board address");
        Assert.That(buffer[ExtruderDriversOffset], Is.EqualTo(6), "extruder driver, local number");
        Assert.That(buffer[ExtruderDriversOffset + 1], Is.EqualTo(3), "extruder driver, board address");

        // Kinematics results and shaping, after the second padding that realigns them
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(ExtruderDriversOffset + (2 * MotionLimits.MaxExtruders))), Is.EqualTo(0), "padding2");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ContinuousRotationAxesOffset)), Is.EqualTo(0x0000_0020));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ControllingDrivesOffset + 4)), Is.EqualTo(0x3));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ShapingTimeClocksOffset)), Is.EqualTo(1234));
    }

    [Test]
    public void UnconfiguredDriversSerialiseAsNoBoard()
    {
        // A default DriverId has no board address, which the native side reads as "not remote" and
        // drops rather than addressing the movement to board zero
        MotionConfig config = new();
        byte[] buffer = new byte[MotionConfig.SerializedLength];
        config.Serialize(buffer);

        Assert.That(buffer[AxisDriversOffset + 2], Is.EqualTo(DriverId.NoCanAddress));
        Assert.That(buffer[ExtruderDriversOffset + 1], Is.EqualTo(DriverId.NoCanAddress));
    }

    [Test]
    public void SerializeRejectsAShortBuffer()
    {
        MotionConfig config = new();
        byte[] tooSmall = new byte[MotionConfig.SerializedLength - 1];
        Assert.Throws<ArgumentException>(() => config.Serialize(tooSmall));
    }
}
