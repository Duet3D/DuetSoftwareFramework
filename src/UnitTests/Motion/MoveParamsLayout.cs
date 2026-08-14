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
        // The header declares Size = 28, which sets the size rather than checking it: a field
        // removed or narrowed leaves the runtime padding the tail out to 28 and the arrays after it
        // still land where the native side looks for them, while every field this side of the gap
        // has moved. Adding the fields up is what catches that
        int size = PackedStructSize.OfFields(typeof(MoveParamsHeader));

        Assert.Multiple(() =>
        {
            Assert.That(size, Is.EqualTo(Marshal.SizeOf<MoveParamsHeader>()), "the fields fill the header, leaving no padding");
            Assert.That(size, Is.EqualTo(28), "sizeof(MoveParamsHeader)");
        });
    }
}
