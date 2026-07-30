using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for the generated bit-field helpers. The generated conformance fixture in
/// <c>CanMessageLayout.g.cs</c> only exercises the widths that the schema actually declares, so the
/// sign-extension helpers are covered here across the whole range of widths they support.
/// </summary>
[TestFixture]
public class CanBitFieldsTests
{
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(24)]
    [TestCase(31)]
    [TestCase(32)]
    public void SignExtendRoundTripsTheEdgesOfEachWidth(int width)
    {
        ulong allOnes = (1UL << width) - 1;
        ulong signBit = 1UL << (width - 1);
        int mostNegative = width >= 32 ? int.MinValue : (int)-(1L << (width - 1));
        int mostPositive = width >= 32 ? int.MaxValue : (int)((1L << (width - 1)) - 1);

        Assert.That(CanBitFields.SignExtend(allOnes, width), Is.EqualTo(-1), "all ones is -1");
        Assert.That(CanBitFields.SignExtend(0, width), Is.EqualTo(0), "zero is 0");
        Assert.That(CanBitFields.SignExtend(signBit, width), Is.EqualTo(mostNegative), "sign bit alone is the most negative value");
        if (width > 1)
        {
            Assert.That(CanBitFields.SignExtend(signBit - 1, width), Is.EqualTo(mostPositive), "sign bit clear is the most positive value");
        }
    }

    [TestCase(1)]
    [TestCase(24)]
    [TestCase(32)]
    [TestCase(33)]
    [TestCase(40)]
    [TestCase(63)]
    [TestCase(64)]
    public void SignExtend64RoundTripsTheEdgesOfEachWidth(int width)
    {
        ulong allOnes = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong signBit = 1UL << (width - 1);
        long mostNegative = width >= 64 ? long.MinValue : -(1L << (width - 1));
        long mostPositive = width >= 64 ? long.MaxValue : (1L << (width - 1)) - 1;

        Assert.That(CanBitFields.SignExtend64(allOnes, width), Is.EqualTo(-1L), "all ones is -1");
        Assert.That(CanBitFields.SignExtend64(0, width), Is.EqualTo(0L), "zero is 0");
        Assert.That(CanBitFields.SignExtend64(signBit, width), Is.EqualTo(mostNegative), "sign bit alone is the most negative value");
        if (width > 1)
        {
            Assert.That(CanBitFields.SignExtend64(signBit - 1, width), Is.EqualTo(mostPositive), "sign bit clear is the most positive value");
        }
    }

    /// <summary>
    /// A field wider than 32 bits must not be routed through the int overload: it truncates the value and
    /// drops the sign. That is the mistake the generator used to make for signed bitfields over 32 bits,
    /// whose properties it typed as long while extending them through the int helper.
    /// </summary>
    [Test]
    public void SignExtendIntOverloadCannotRepresentWideFields()
    {
        const int width = 40;
        ulong raw = (1UL << (width - 1)) | 1;                   // most negative 40-bit value, plus one bit
        const long expected = -(1L << (width - 1)) + 1;

        Assert.That(CanBitFields.SignExtend64(raw, width), Is.EqualTo(expected));
        Assert.That(CanBitFields.SignExtend(raw, width), Is.Not.EqualTo(expected));
    }
}
