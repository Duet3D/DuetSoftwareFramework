using DuetAPI.ObjectModel;
using OmDriverId = DuetAPI.Utility.DriverId;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Tests for how an endstop is named on the CAN bus
/// </summary>
/// <remarks>
/// Three places have to agree on this and none of them talks to the others: M574 asks a board to
/// watch an input under a handle, a homing move tells the controller which handle stops which drive,
/// and the receiver turns an incoming change back into an endstop. They agree only because the
/// handle is derived from the axis the same way every time, so that derivation is worth pinning down
/// </remarks>
[TestFixture]
public class RemoteEndstopsTests
{
    [Test]
    public void TheHandleIdentifiesTheAxisAsAnEndstop()
    {
        RemoteInputHandle handle = RemoteEndstops.HandleFor(2);
        Assert.Multiple(() =>
        {
            Assert.That(handle.Type, Is.EqualTo(RemoteInputHandle.TypeEndstop), "a general-purpose input would be applied elsewhere");
            Assert.That(handle.Major, Is.EqualTo(2), "major is the axis");
            Assert.That(handle.Minor, Is.EqualTo(0), "the axis' only switch");
        });
    }

    [Test]
    public void EachSwitchOfAnAxisGetsItsOwnHandle()
    {
        // A gantry squares itself by letting each motor run on to its own switch, so the two switches
        // have to be distinguishable - sharing a handle would stop both motors on whichever fired first
        Assert.That(RemoteEndstops.HandleFor(2, 1).Minor, Is.EqualTo(1), "minor is the switch within the axis");
        Assert.That(RemoteEndstops.HandleFor(2, 1).Major, Is.EqualTo(2), "still the same axis");
        Assert.That(RemoteEndstops.HandleFor(2, 0).All, Is.Not.EqualTo(RemoteEndstops.HandleFor(2, 1).All));
    }

    [Test]
    public void EveryAxisGetsADistinctHandle()
    {
        // Two axes sharing a handle would mean one axis' endstop stopping the other
        Assert.That(RemoteEndstops.HandleFor(0).All, Is.Not.EqualTo(RemoteEndstops.HandleFor(1).All));
    }

    [TestCase("0.io1.in", (byte)0, "io1.in")]
    [TestCase("3.io2.in", (byte)3, "io2.in")]
    [TestCase("io1.in", (byte)0, "io1.in")]
    public void APortNamesTheBoardThatCarriesIt(string port, byte expectedBoard, string expectedLocal)
    {
        Assert.That(RemoteEndstops.TrySplitPort(port, out byte board, out string local), Is.True);
        Assert.That(board, Is.EqualTo(expectedBoard));
        Assert.That(local, Is.EqualTo(expectedLocal), "the board keeps the name it knows the port by");
    }

    [Test]
    public void AStopInputCarriesBothTheBoardAndTheHandle()
    {
        // The controller matches an incoming change on both, so losing either stops the wrong drive
        Endstop endstop = new() { Type = EndstopType.InputPin, Port = "3.io2.in" };
        MoveStopInput stopInput = new();
        Assert.That(RemoteEndstops.TryGetStopInput(endstop, 1, 1, stopInput), Is.True);
        Assert.That(stopInput.NumSwitches, Is.EqualTo(1), "the whole axis stops on the one switch");
        Assert.That(stopInput.Boards[0], Is.EqualTo(3), "the board survives");
        Assert.That(stopInput.Handle, Is.EqualTo(RemoteEndstops.HandleFor(1).All), "the handle survives");
    }

    [Test]
    public void AnEndstopAMoveCannotStopOnIsRefused()
    {
        // A stall endstop is detected by the driver rather than by an input, and a Z probe standing
        // in for an endstop needs M558. Neither can be expressed as an input to watch
        Assert.Multiple(() =>
        {
            Assert.That(RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.MotorStallAny, Port = "0.io1.in" }, 0, 1, new MoveStopInput()), Is.False);
            Assert.That(RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.ZProbeAsEndstop }, 0, 1, new MoveStopInput()), Is.False);
            Assert.That(RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.InputPin }, 0, 1, new MoveStopInput()), Is.False, "no port named");
        });
    }

    // RepRapFirmware picks between stopping one driver, one axis, or everything, and which it picks
    // depends on the geometry rather than on the endstop. The deciding question is whether moving an
    // axis needs drives other than its own, which is what the engines answer below - the same
    // property RRF's SwitchEndstop::PrimeAxis tests.
    private static bool StoppingTheAxisStopsEverything(string geometry, int axis)
    {
        KinematicsEngine engine = CoreKinematicsEngine.TryCreate(geometry)!;
        return (engine.GetControllingDrives(axis) & ~(1u << axis)) != 0;
    }

    [Test]
    public void AnIndependentAxisStopsOnlyItself()
    {
        // On a Cartesian each axis has its own motor, so stopping that motor is the whole job
        Assert.That(StoppingTheAxisStopsEverything("cartesian", 0), Is.False, "X");
        Assert.That(StoppingTheAxisStopsEverything("cartesian", 1), Is.False, "Y");
    }

    [Test]
    public void ACoupledAxisHasToStopEverything()
    {
        // Holding X still on a CoreXY needs both motors, so stopping only "X's drivers" would leave
        // the other one running and drag the head diagonally into the switch
        Assert.That(StoppingTheAxisStopsEverything("corexy", 0), Is.True, "X");
        Assert.That(StoppingTheAxisStopsEverything("corexy", 1), Is.True, "Y");

        // Z is still its own motor even on a CoreXY, so homing it need not stop everything
        Assert.That(StoppingTheAxisStopsEverything("corexy", 2), Is.False, "Z");
    }

    [Test]
    public void OneSwitchPerDriverIsAskedForOnlyWhenTheCountsMatch()
    {
        // RepRapFirmware stops each driver on its own switch when an axis has as many switches as
        // drivers, and falls back to stopping the whole axis on the first trigger when it does not.
        // The fallback is what makes a dual-motor axis with a single switch safe: neither motor can
        // be left running because its own switch never fires
        Endstop two = new() { Type = EndstopType.InputPin, Port = "1.io1.in+1.io2.in" };
        MoveStopInput stop = new();

        Assert.That(RemoteEndstops.TryGetStopInput(two, 2, 2, stop), Is.True);
        Assert.That(stop.NumSwitches, Is.EqualTo(2), "two switches, two drivers");

        Assert.That(RemoteEndstops.TryGetStopInput(two, 2, 3, stop), Is.True);
        Assert.That(stop.NumSwitches, Is.EqualTo(1), "a driver with no switch would never stop");

        Endstop one = new() { Type = EndstopType.InputPin, Port = "1.io1.in" };
        Assert.That(RemoteEndstops.TryGetStopInput(one, 2, 2, stop), Is.True);
        Assert.That(stop.NumSwitches, Is.EqualTo(1), "one switch stops the whole axis");

        Assert.That(RemoteEndstops.TryGetStopInput(one, 2, 1, stop), Is.True);
        Assert.That(stop.NumSwitches, Is.EqualTo(1), "a single-motor axis has nothing to split");
    }

    [Test]
    public void ThePerDriverSwitchesNeedNotShareABoard()
    {
        // Each switch carries its own CAN address, as RepRapFirmware's SwitchEndstop keeps a board
        // number per port. A gantry whose two motors sit on two expansion boards is wired this way
        Endstop split = new() { Type = EndstopType.InputPin, Port = "1.io1.in+2.io1.in" };
        MoveStopInput stop = new();
        Assert.That(RemoteEndstops.TryGetStopInput(split, 2, 2, stop), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(stop.NumSwitches, Is.EqualTo(2));
            Assert.That(stop.Boards[0], Is.EqualTo(1), "the first motor's switch");
            Assert.That(stop.Boards[1], Is.EqualTo(2), "the second motor's switch, on another board");
        });
    }

    [Test]
    public void TheSwitchesOfAnAxisAreListedInDriverOrder()
    {
        // Port i belongs to driver i, which is how RepRapFirmware pairs them, so the order the ports
        // were written in is the order the drivers are configured in
        Endstop endstop = new() { Type = EndstopType.InputPin, Port = "1.io1.in+1.io2.in" };
        Assert.That(RemoteEndstops.PortsOf(endstop), Is.EqualTo(new[] { "1.io1.in", "1.io2.in" }));
        Assert.That(RemoteEndstops.PortsOf(new Endstop()), Is.Empty, "an endstop with no port has no switches");
    }

    [Test]
    public void ARefusedEndstopYieldsTheSentinel()
    {
        // The caller writes the result into the move either way, so a refusal has to be the value
        // that means "watch nothing" rather than a stale one
        MoveStopInput stopInput = new();
        stopInput.SetShared(0x1234, 5);
        RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.MotorStallAny }, 0, 1, stopInput);
        Assert.That(stopInput.NumSwitches, Is.Zero, "a refused endstop leaves the drive watching nothing");
    }

    [Test]
    public void AStallEndstopIsOneHandleAndABoardPerDriver()
    {
        // A board reports every driver that stalled under one board-wide handle, so what tells one
        // driver's stall from another's is the board. That is the opposite way round from a switch
        // per driver, where the handle's minor field selects the switch and the boards may repeat
        MoveStopInput stopInput = new();
        OmDriverId[] drivers = [new OmDriverId(1, 0), new OmDriverId(4, 2)];

        Assert.That(RemoteEndstops.TryGetStallStopInput(drivers, stopInput), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(stopInput.Handle, Is.EqualTo(RemoteEndstops.StallHandle().All));
            Assert.That(stopInput.NumSwitches, Is.EqualTo(2));
            Assert.That(stopInput.Boards[0], Is.EqualTo(1), "the first driver's board");
            Assert.That(stopInput.Boards[1], Is.EqualTo(4), "the second driver's board");
        });
    }

    [Test]
    public void AStallEndstopOnOneDriverStopsEveryDriverOfTheDrive()
    {
        // Written as shared rather than as a one-entry per-driver list, so that a dual-motor axis
        // with one stall-detecting driver still stops both motors
        MoveStopInput stopInput = new();
        Assert.That(RemoteEndstops.TryGetStallStopInput([new OmDriverId(2, 1)], stopInput), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(stopInput.NumSwitches, Is.EqualTo(1), "shared, so every driver watches it");
            Assert.That(stopInput.Boards[0], Is.EqualTo(2));
        });

        Assert.That(RemoteEndstops.TryGetStallStopInput([], stopInput), Is.False, "nothing to watch");
        Assert.That(stopInput.NumSwitches, Is.Zero);
    }

    [Test]
    public void TheStallHandleIsBoardWideRatherThanPerAxis()
    {
        // Duet3Expansion reports stalls under RemoteInputHandle(typeStallEndstop, 0, 0) whatever the
        // driver, so neither field may vary or the move would name a handle no board ever reports
        RemoteInputHandle handle = RemoteEndstops.StallHandle();
        Assert.Multiple(() =>
        {
            Assert.That(handle.Type, Is.EqualTo(RemoteInputHandle.TypeStallEndstop));
            Assert.That(handle.Major, Is.Zero);
            Assert.That(handle.Minor, Is.Zero);
        });
    }
}
