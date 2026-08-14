using System.Collections.Generic;
using System.Linq;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Which input monitors a reconfiguration gives back
/// </summary>
/// <remarks>
/// A board asked to watch a pin holds it until told otherwise, and holding it means keeping it
/// claimed - so a pin abandoned by M574 or M558 cannot be given to anything else afterwards. What
/// decides is the difference between what the old configuration had watched and what the new one
/// will, which is what these pin down; the sending is a CAN round trip and belongs to a machine
/// </remarks>
[TestFixture]
public class InputMonitorsTests
{
    private static Endstop Switch(string port) => new() { Type = EndstopType.InputPin, Port = port };

    private static IEnumerable<ushort> Handles(IEnumerable<InputMonitors.Monitored> monitors)
        => monitors.Select(monitor => monitor.Handle.All);

    /// <summary>What is deleted when an axis goes from one configuration to another</summary>
    private static List<InputMonitors.Monitored> Dropped(Endstop? before, Endstop? after, int axis = 0)
    {
        List<InputMonitors.Monitored> was = InputMonitors.Of(before, axis), now = InputMonitors.Of(after, axis);
        return was.Where(monitor => !now.Any(kept => kept.Board == monitor.Board
                                                     && kept.Handle.All == monitor.Handle.All)).ToList();
    }

    [Test]
    public void ASwitchIsWatchedUnderItsOwnAxisAndBoard()
    {
        List<InputMonitors.Monitored> monitors = InputMonitors.Of(Switch("2.io1.in"), 1);

        Assert.Multiple(() =>
        {
            Assert.That(monitors, Has.Count.EqualTo(1));
            Assert.That(monitors[0].Board, Is.EqualTo(2), "the board named by the port");
            Assert.That(monitors[0].Handle.All, Is.EqualTo(RemoteEndstops.HandleFor(1).All));
        });
    }

    [Test]
    public void EachSwitchOfAnAxisIsItsOwnMonitor()
    {
        List<InputMonitors.Monitored> monitors = InputMonitors.Of(Switch("1.io0.in+2.io0.in"), 0);

        Assert.Multiple(() =>
        {
            Assert.That(monitors.Select(monitor => monitor.Board), Is.EqualTo(new[] { 1, 2 }),
                        "the switches of an axis need not share a board");
            Assert.That(Handles(monitors), Is.EqualTo(new[] { RemoteEndstops.HandleFor(0, 0).All,
                                                              RemoteEndstops.HandleFor(0, 1).All }));
        });
    }

    [Test]
    public void OnlyASwitchOnAPinIsWatched()
    {
        // A stall is detected by the driver and a Z probe endstop is watched under the probe's handle,
        // so neither has a monitor of its own to give back
        Assert.Multiple(() =>
        {
            Assert.That(InputMonitors.Of(new Endstop { Type = EndstopType.MotorStallAny, Port = "1.io0.in" }, 0), Is.Empty);
            Assert.That(InputMonitors.Of(new Endstop { Type = EndstopType.ZProbeAsEndstop, Port = "1.io0.in" }, 0), Is.Empty);
            Assert.That(InputMonitors.Of((Endstop?)null, 0), Is.Empty, "and an axis with no endstop has none");
        });
    }

    [Test]
    public void RemovingAnEndstopGivesItsPinBack()
    {
        // M574 X0
        Assert.That(Dropped(Switch("1.io0.in"), null), Has.Count.EqualTo(1));
    }

    [Test]
    public void ChangingToAStallEndstopGivesThePinBack()
    {
        // M574 X1 S3 - the axis keeps an endstop, but not one an input monitor can express
        Assert.That(Dropped(Switch("1.io0.in"), new Endstop { Type = EndstopType.MotorStallAny }), Has.Count.EqualTo(1));
    }

    [Test]
    public void ClearingThePortGivesItBack()
    {
        // M574 X1 S1 P"" - how an endstop is given up while the axis keeps its slot
        Assert.That(Dropped(Switch("1.io0.in"), Switch("")), Has.Count.EqualTo(1));
    }

    [Test]
    public void DroppingOneSwitchOfSeveralGivesBackOnlyThatOne()
    {
        // P"a+b" to P"a": the axis still has an endstop, so nothing above notices, but the second
        // switch's handle is abandoned
        List<InputMonitors.Monitored> dropped = Dropped(Switch("1.io0.in+2.io0.in"), Switch("1.io0.in"));

        Assert.Multiple(() =>
        {
            Assert.That(dropped, Has.Count.EqualTo(1));
            Assert.That(dropped[0].Handle.All, Is.EqualTo(RemoteEndstops.HandleFor(0, 1).All), "the one that went");
            Assert.That(dropped[0].Board, Is.EqualTo(2));
        });
    }

    [Test]
    public void MovingAnEndstopToAnotherPinOnTheSameBoardGivesNothingBack()
    {
        // Creating a monitor replaces any monitor under the same handle, so this needs no delete -
        // and sending one would drop the monitor that was just created
        Assert.That(Dropped(Switch("1.io0.in"), Switch("1.io5.in")), Is.Empty);
    }

    [Test]
    public void MovingAnEndstopToAnotherBoardGivesTheOldBoardsPinBack()
    {
        // The handle is the same, but it is a different board holding a different pin, and only the
        // board named by the new port will have its monitor replaced
        List<InputMonitors.Monitored> dropped = Dropped(Switch("1.io0.in"), Switch("2.io0.in"));

        Assert.Multiple(() =>
        {
            Assert.That(dropped, Has.Count.EqualTo(1));
            Assert.That(dropped[0].Board, Is.EqualTo(1), "the board that is no longer watching anything");
        });
    }

    [Test]
    public void AProbeIsWatchedUnderItsProbeNumber()
    {
        List<InputMonitors.Monitored> monitors =
            InputMonitors.Of(new Probe { Type = ProbeType.Digital, Port = "3.io2.in" }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(monitors, Has.Count.EqualTo(1));
            Assert.That(monitors[0].Board, Is.EqualTo(3));
            Assert.That(monitors[0].Handle.All, Is.EqualTo(RemoteProbes.HandleFor(1).All));
        });
    }

    [Test]
    public void SettingAProbeToTypeNoneGivesItsPinBack()
    {
        // M558 P0. Nothing is created for a probe of type none, so without this the pin is held by a
        // probe that no longer exists
        List<InputMonitors.Monitored> was = InputMonitors.Of(new Probe { Type = ProbeType.Digital, Port = "1.io0.in" }, 0);
        List<InputMonitors.Monitored> now = InputMonitors.Of(new Probe { Type = ProbeType.None, Port = "1.io0.in" }, 0);

        Assert.Multiple(() =>
        {
            Assert.That(was, Has.Count.EqualTo(1));
            Assert.That(now, Is.Empty);
        });
    }

    [Test]
    public void AStallProbeHasNoPinToGiveBack()
    {
        Assert.That(InputMonitors.Of(new Probe { Type = ProbeType.ZMotorStall, Port = "1.io0.in" }, 0), Is.Empty);
    }
}
