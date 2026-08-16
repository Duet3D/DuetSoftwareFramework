using System;
using System.Reflection;
using System.Runtime.InteropServices;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using UnitTests.Utility;

namespace UnitTests.Motion;

/// <summary>
/// The serialised form of a move submission against the native structs it is read back as
/// </summary>
/// <remarks>
/// The record is a <see cref="MoveParamsHeader"/> followed by three arrays indexed by logical drive,
/// and the native side walks it by pointer arithmetic: it is handed a length and an entry size and
/// trusts both. Nothing on either side notices an entry that grew here and not there - the drives
/// simply come out shifted, so a homing move watches another drive's switch. The numbers below are
/// the ones <c>tests/MoveParamsLayoutTests.cpp</c> and the <c>static_assert</c>s in
/// <c>Motion/MoveParams.h</c> hold the native side to
/// </remarks>
[TestFixture]
public class MoveParamsLayout
{
    [Test]
    public void MoveStopInputLengthCorrect()
    {
        // The entry is a managed class written field by field, so there is no unmanaged size to ask
        // the runtime for. Add up its fields instead, rather than restating the number the constant
        // already says
        MoveStopInput stop = new();
        int size = 0;
        foreach (PropertyInfo property in typeof(MoveStopInput).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Boards is the one field whose width is a count rather than a type, so it is measured
            // from the instance. An enum is as wide as what it is declared over, which is not what
            // Marshal.SizeOf will say about it; everything else is as wide as it marshals
            size += property.GetValue(stop) switch
            {
                byte[] boards => boards.Length,
                _ => Marshal.SizeOf(property.PropertyType.IsEnum
                                    ? Enum.GetUnderlyingType(property.PropertyType)
                                    : property.PropertyType)
            };
        }

        Assert.Multiple(() =>
        {
            Assert.That(MoveStopInput.Length, Is.EqualTo(size), "Length covers every field of the entry");

            // 6 + maxDriversPerAxis is what MoveParams.h static_asserts sizeof(MoveStopInput) to be,
            // and MotionConfigLayout pins maxDriversPerAxis itself to the native 8
            Assert.That(size, Is.EqualTo(6 + MotionLimits.MaxDriversPerAxis), "sizeof(MoveStopInput)");
        });
    }

    [Test]
    public void MoveParamsHeaderFillsItsDeclaredSize()
    {
        // The header declares its size, which sets it rather than checking it: a field removed or
        // narrowed leaves the runtime padding the tail out and the arrays after it still land where
        // the native side looks for them, while every field this side of the gap has moved. Adding
        // the fields up is what catches that
        int size = PackedStructSize.OfFields(typeof(MoveParamsHeader));

        Assert.Multiple(() =>
        {
            Assert.That(size, Is.EqualTo(Marshal.SizeOf<MoveParamsHeader>()), "the fields fill the header, leaving no padding");
            Assert.That(size, Is.EqualTo(40), "sizeof(MoveParamsHeader)");
        });
    }

    [Test]
    public void MoveDriveTuningFillsItsDeclaredSize()
    {
        // Same trap as the header, and it matters more here: this one is copied as a block, so a
        // field that has moved corrupts every drive of every move rather than failing loudly
        int size = PackedStructSize.OfFields(typeof(MoveDriveTuning));

        Assert.Multiple(() =>
        {
            Assert.That(size, Is.EqualTo(Marshal.SizeOf<MoveDriveTuning>()), "the fields fill the entry, leaving no padding");
            Assert.That(size, Is.EqualTo(28), "sizeof(MoveDriveTuning)");
        });
    }

    [Test]
    public void SubmissionLengthCoversAllFourArrays()
    {
        const int numDrives = 4;

        Assert.That(MoveParams.Length(numDrives),
                    Is.EqualTo(40 + (numDrives * (sizeof(int) + sizeof(float) + MoveStopInput.Length + 28))),
                    "a submission is the header plus four per-drive arrays");
    }

    [Test]
    public void TuningIsWrittenAfterTheStopInputs()
    {
        // The tuning array is the last thing in the record, so its first entry begins exactly where
        // the stop inputs end. Getting that wrong would have the engine read pressure advance out of
        // somebody's endstop configuration
        const int numDrives = 2;
        MoveParamsHeader header = new() { NumDrives = numDrives };
        int[] endPoints = new int[numDrives];
        float[] directions = new float[numDrives];
        MoveStopInput[] stops = [new(), new()];
        MoveDriveTuning[] tuning =
        [
            new() { InstantDv = 1.5f, PressureAdvanceClocks = 30.0f, BacklashSteps = 11 },
            new() { InstantDv = 2.5f, PressureAdvanceClocks = 40.0f, BacklashSteps = 22 }
        ];

        byte[] buffer = new byte[MoveParams.Length(numDrives)];
        int written = MoveParams.Write(buffer, header, endPoints, directions, stops, tuning);

        int tuningOffset = 40 + (numDrives * (sizeof(int) + sizeof(float) + MoveStopInput.Length));
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(buffer.Length));
            Assert.That(BitConverter.ToSingle(buffer, tuningOffset), Is.EqualTo(1.5f), "drive 0 jerk limit");
            Assert.That(BitConverter.ToSingle(buffer, tuningOffset + 8), Is.EqualTo(30.0f), "drive 0 pressure advance");
            Assert.That(BitConverter.ToInt32(buffer, tuningOffset + 12), Is.EqualTo(11), "drive 0 backlash");
            Assert.That(BitConverter.ToSingle(buffer, tuningOffset + 28), Is.EqualTo(2.5f), "drive 1 jerk limit");
        });
    }
}
