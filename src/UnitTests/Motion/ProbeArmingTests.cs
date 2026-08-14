using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// What a probe's board is told when a tap starts
/// </summary>
/// <remarks>
/// A probe is configured once by M558 and then used many times, and two of the things the board was
/// told at configuration time go stale: the threshold, which G31 P may have changed since, and the
/// reporting interval, which should only be fast while probing. Both are pushed around the tap, as
/// RepRapFirmware's <c>RemoteZProbe::SetProbing</c> does, and what is captured to push is what these
/// pin down - the sending itself is a CAN round trip and belongs to a machine
/// </remarks>
[TestFixture]
public class ProbeArmingTests
{
    private static Probe Probe(ProbeType type, string? port = "1.io3.in", int threshold = 500)
        => new() { Type = type, Port = port, Threshold = threshold };

    [Test]
    public void AnAnalogProbeIsToldTheThresholdItHasNow()
    {
        // The point of pushing it per tap rather than at M558 time: G31 P writes the object model,
        // and this is what carries that to the board
        Probe probe = Probe(ProbeType.Analog, threshold: 42);

        Assert.That(ProbeArming.TryCapture(probe, 0, out ProbeArming.ProbeMonitor monitor), Is.True);
        Assert.That(monitor.Threshold, Is.EqualTo(42u));
    }

    [Test]
    public void ADigitalProbeIsToldNoThreshold()
    {
        // A nonzero threshold is what tells the board to read the pin as analog, so sending one to a
        // digital probe would stop it reporting at all
        Assert.That(ProbeArming.TryCapture(Probe(ProbeType.Digital), 0, out ProbeArming.ProbeMonitor monitor), Is.True);
        Assert.That(monitor.Threshold, Is.Null);
    }

    [Test]
    public void AScanningProbeIsToldTheThreshold()
    {
        Assert.That(ProbeArming.TryCapture(Probe(ProbeType.ScanningAnalog), 0, out ProbeArming.ProbeMonitor monitor), Is.True);
        Assert.That(monitor.Threshold, Is.Not.Null);
    }

    [Test]
    public void TheBoardCarryingThePortIsTheOneTold()
    {
        Assert.That(ProbeArming.TryCapture(Probe(ProbeType.Digital, "3.io0.in"), 0, out ProbeArming.ProbeMonitor monitor),
                    Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(monitor.Board, Is.EqualTo(3));
            Assert.That(monitor.ProbeNumber, Is.EqualTo(0), "which is what the handle is derived from");
        });
    }

    [Test]
    public void AProbeWithNoInputIsNotArmed()
    {
        // Nothing was created for any of these, so there is no handle to change. A motor stall probe
        // is the one that matters: it is detected by the driver, and it has its own arming beside this
        Assert.Multiple(() =>
        {
            Assert.That(ProbeArming.TryCapture(Probe(ProbeType.ZMotorStall, port: null), 0, out _), Is.False,
                        "a stall probe is not an input");
            Assert.That(ProbeArming.TryCapture(Probe(ProbeType.None), 0, out _), Is.False,
                        "nor is a placeholder for manual probing");
            Assert.That(ProbeArming.TryCapture(Probe(ProbeType.Digital, port: null), 0, out _), Is.False,
                        "nor is a probe whose port has not been given yet");
            Assert.That(ProbeArming.TryCapture(Probe(ProbeType.Digital, port: "   "), 0, out _), Is.False,
                        "nor a blank one");
        });
    }

    [Test]
    public void AStallProbeWithAPortIsStillNotArmed()
    {
        // M558 P10 takes no port, but the object model can hold one from a previous M558. What
        // decides is the type, since a stall is detected by the driver whatever port is named
        Assert.That(ProbeArming.TryCapture(Probe(ProbeType.ZMotorStall), 0, out _), Is.False);
    }

    [Test]
    public void ANegativeThresholdIsNotSentAsAHugeOne()
    {
        // The threshold travels as an unsigned parameter, and G31 P takes an int
        Assert.That(ProbeArming.TryCapture(Probe(ProbeType.Analog, threshold: -1), 0, out ProbeArming.ProbeMonitor monitor),
                    Is.True);
        Assert.That(monitor.Threshold, Is.EqualTo(0u));
    }

    [Test]
    public void ProbingIsFasterThanNotProbing()
    {
        // The whole point of having two intervals. RepRapFirmware's 2 ms and 25 ms
        Assert.Multiple(() =>
        {
            Assert.That(ProbeArming.ActiveReportInterval, Is.EqualTo(2u));
            Assert.That(ProbeArming.InactiveReportInterval, Is.EqualTo(25u));
            Assert.That(ProbeArming.ActiveReportInterval, Is.LessThan(ProbeArming.InactiveReportInterval));
        });
    }
}
