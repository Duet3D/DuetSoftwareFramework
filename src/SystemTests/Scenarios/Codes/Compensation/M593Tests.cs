using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M593, input shaping</summary>
[TestFixture]
[GCode("M593")]
public class M593Tests : BenchFixture
{
    /// <summary>
    /// M593 P"zvd" publishes the shaper type, frequency and damping, and the three ZVD impulses
    /// with their delays; M593 P"none" disables shaping down to a single unit impulse
    /// </summary>
    /// <remarks>
    /// AxisShaper::Configure in Movement/AxisShaper.cpp: for zvd, k = exp(-zeta*pi/sqrt(1-zeta^2)),
    /// j = (1+k)^2, amplitudes 1/j and 2k/j with the last impulse filling to a sum of 1, delays at
    /// half and one damped period. OM fields (type, frequency, damping, amplitudes, delays in
    /// seconds) from the AxisShaper object model tables in the same file
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M593ConfiguresInputShaping()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M593 P\"zvd\" F50 S0.2");

        const double zeta = 0.2;
        const double frequency = 50.0;
        double sqrtOneMinusZetaSquared = Math.Sqrt(1.0 - zeta * zeta);
        double dampedFrequency = frequency * sqrtOneMinusZetaSquared;
        double k = Math.Exp(-zeta * Math.PI / sqrtOneMinusZetaSquared);
        double j = (1.0 + k) * (1.0 + k);

        InputShapingType type = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Type);
        double reportedFrequency = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Frequency);
        double damping = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Damping);
        double amplitudeCount = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Amplitudes.Count);
        double delayCount = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Delays.Count);
        Assert.Multiple(() =>
        {
            Assert.That(type, Is.EqualTo(InputShapingType.ZVD), "M593 P sets move.shaping.type (RRF AxisShaper::Configure)");
            Assert.That(reportedFrequency, Is.EqualTo(50).Within(1e-2), "M593 F sets move.shaping.frequency (Hz)");
            Assert.That(damping, Is.EqualTo(0.2).Within(1e-3), "M593 S sets move.shaping.damping (zeta)");
            Assert.That(amplitudeCount, Is.EqualTo(3), "a ZVD shaper has three impulses in move.shaping.amplitudes (RRF numImpulses)");
            Assert.That(delayCount, Is.EqualTo(3), "a ZVD shaper has three entries in move.shaping.delays (RRF numImpulses)");
        });

        // The counts guard the indexed reads: an element read on a shorter array cannot evaluate
        double amplitude0 = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Amplitudes[0]);
        double amplitude1 = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Amplitudes[1]);
        double amplitude2 = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Amplitudes[2]);
        double delay0 = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Delays[0]);
        double delay1 = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Delays[1]);
        double delay2 = await bench.Host.ReadModelAsync(model => model.Move.Shaping.Delays[2]);
        Assert.Multiple(() =>
        {
            Assert.That(amplitude0, Is.EqualTo(1.0 / j).Within(2e-3), "move.shaping.amplitudes[0] = 1/(1+k)^2 for zvd");
            Assert.That(amplitude1, Is.EqualTo(2.0 * k / j).Within(2e-3), "move.shaping.amplitudes[1] = 2k/(1+k)^2 for zvd");
            Assert.That(amplitude2, Is.EqualTo(1.0 - (1.0 / j) - (2.0 * k / j)).Within(2e-3), "move.shaping.amplitudes[2] fills the sum to 1");
            Assert.That(delay0, Is.EqualTo(0), "move.shaping.delays[0] is always 0");
            Assert.That(delay1, Is.EqualTo(0.5 / dampedFrequency).Within(2e-4), "move.shaping.delays[1] is half the damped period, in seconds");
            Assert.That(delay2, Is.EqualTo(1.0 / dampedFrequency).Within(2e-4), "move.shaping.delays[2] is one damped period, in seconds");
        });
    }

    /// <summary>
    /// M593 P"none" disables input shaping: the type reports none, the impulse train collapses to
    /// a single unit impulse, and a bare M593 says shaping is disabled
    /// </summary>
    /// <remarks>
    /// AxisShaper::Configure in Movement/AxisShaper.cpp: type none sets numImpulses = 1, and the
    /// report branch answers "Input shaping is disabled"
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M593NoneDisablesInputShaping()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M593 P\"zvd\" F50 S0.2");
        await bench.Host.ExecuteCodeAsync("M593 P\"none\"");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Shaping.Type), Is.EqualTo(InputShapingType.None),
                    "M593 P\"none\" sets move.shaping.type back to none (RRF AxisShaper::Configure)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M593")).Trim(), Is.EqualTo("Input shaping is disabled"),
                    "bare M593 with shaping off reports so (AxisShaper::Configure)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Shaping.Amplitudes.Count), Is.EqualTo(1),
                    "a disabled shaper is a single unit impulse (RRF numImpulses = 1)");
    }
}
