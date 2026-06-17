using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;
using System;

namespace UnitTests.Commands;

public class Parameter
{
    [Test]
    public void UIntArrayToLongArray()
    {
        // Values beyond int.MaxValue are parsed as uint[] and must convert to long[]
        CodeParameter parameter = new('P', "4000000000:1", false, false);
        Assert.That(parameter.Type, Is.EqualTo(typeof(uint[])));

        long[] longArray = (long[])parameter;
        Assert.That(longArray, Is.EqualTo(new long[] { 4000000000L, 1L }));
    }

    [Test]
    public void EqualityOfNullParameters()
    {
        // Parameters without a value (e.g. G28 X) must compare equal to themselves and to each other
        CodeParameter a = new('X', string.Empty, false, false), b = new('X', string.Empty, false, false);
        Assert.That(a.IsNull, Is.True);
#pragma warning disable CS1718
        Assert.That(a == a, Is.True);
#pragma warning restore CS1718
        Assert.That(a == b, Is.True);

        // A parameter without a value also equals null
        Assert.That(a == null, Is.True);
    }

    [Test]
    public void InvalidDriverId()
    {
        Assert.Throws<ArgumentException>(() => new DriverId("1.2.3"));
        Assert.Throws<ArgumentException>(() => new DriverId("foo"));

        DriverId valid = new("1.2");
        Assert.That(valid.Board, Is.EqualTo(1));
        Assert.That(valid.Port, Is.EqualTo(2));
    }
}
