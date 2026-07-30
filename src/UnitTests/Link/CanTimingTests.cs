using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for the generated CanTiming helpers.
/// </summary>
/// <remarks>
/// The probe proves the layout and the method-surface check proves the signatures, but neither runs a line
/// of these bodies. They are the only generated method bodies that do arithmetic worth getting wrong — the
/// sample point is applied as a 1024ths fixed-point multiply so that the SAMC21 bootloader needs no
/// floating-point maths — so the expected numbers here are worked through by hand from CANlib's definitions.
/// </remarks>
[TestFixture]
public class CanTimingTests
{
    [Test]
    public void SetDefaultsDerivesTheTimingFromTheBitRate()
    {
        CanTiming timing = default;
        timing.SetDefaults(CanTiming.DefaultCanBitRate);

        // 48 MHz / 1 Mbit = 48 quanta; the sample point is 0.78 taken as 798/1024, so 48*798/1024 = 37, less 1
        Assert.That(timing.Period, Is.EqualTo(48));
        Assert.That(timing.NTseg1, Is.EqualTo(36));
        Assert.That(timing.NJumpWidth, Is.EqualTo(11), "the maximum, as recommended by CiA");
        Assert.That(timing.DataRateMultiplier, Is.EqualTo(0x0F), "BRS disabled");
        Assert.That(timing.IsValid(), Is.True);
        Assert.That(timing.IsUsingBrs(), Is.False);
    }

    [Test]
    public void SetDefaultsHandlesASlowerBitRate()
    {
        CanTiming timing = default;
        timing.SetDefaults(500_000);

        Assert.That(timing.Period, Is.EqualTo(96));
        Assert.That(timing.NTseg1, Is.EqualTo(73));         // 96 * 798 / 1024 = 74, less 1
        Assert.That(timing.NJumpWidth, Is.EqualTo(22));
        Assert.That(timing.IsValid(), Is.True);
    }

    [Test]
    public void EnableBrsSetsTheDataPhaseAndReportsItsUse()
    {
        CanTiming timing = default;
        timing.SetDefaults(CanTiming.DefaultCanBitRate);
        timing.EnableBrs(2);

        Assert.That(timing.DataRateMultiplier, Is.EqualTo(1), "the multiplier is stored less one");
        Assert.That(timing.DTseg1, Is.EqualTo(17));         // 24 * 798 / 1024 = 18, less 1
        Assert.That(timing.DJumpWidth, Is.EqualTo(6));
        Assert.That(timing.IsUsingBrs(), Is.True);
    }

    [Test]
    public void SamplePointAndJumpWidthCanBeSetDirectly()
    {
        CanTiming timing = default;
        timing.SetDefaults(CanTiming.DefaultCanBitRate);

        timing.SetNormalSamplePoint(0.5f);
        Assert.That(timing.NTseg1, Is.EqualTo(23));         // 48 * 0.5 = 24, less 1
        Assert.That(timing.NJumpWidth, Is.EqualTo(24));

        // The jump width is clamped to what is left of the bit after the sample point
        timing.SetNormalJumpWidth(1.0f);
        Assert.That(timing.NJumpWidth, Is.EqualTo(24));
        timing.SetNormalJumpWidth(0.0f);
        Assert.That(timing.NJumpWidth, Is.EqualTo(1), "clamped up to at least 1");
    }

    [Test]
    public void RejectsTimingsOutsideTheAllowedRange()
    {
        CanTiming timing = default;
        Assert.That(timing.IsValid(), Is.False, "an all-zero timing has no period");

        timing.SetDefaults(CanTiming.DefaultCanBitRate);
        timing.Period = 23;
        Assert.That(timing.IsValid(), Is.False, "below the minimum period");

        timing.Period = 4801;
        Assert.That(timing.IsValid(), Is.False, "above the maximum period");
    }
}
