using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Motion;
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
            Assert.That(handle.Minor, Is.EqualTo(0), "one switch per axis so far");
        });
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
        Assert.That(RemoteEndstops.TryGetStopInput(endstop, 1, out uint stopInput), Is.True);
        Assert.That(stopInput >> 16, Is.EqualTo(3), "the board survives");
        Assert.That(stopInput & 0xFFFF, Is.EqualTo(RemoteEndstops.HandleFor(1).All), "the handle survives");
    }

    [Test]
    public void AnEndstopAMoveCannotStopOnIsRefused()
    {
        // A stall endstop is detected by the driver rather than by an input, and a Z probe standing
        // in for an endstop needs M558. Neither can be expressed as an input to watch
        Assert.Multiple(() =>
        {
            Assert.That(RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.MotorStallAny, Port = "0.io1.in" }, 0, out _), Is.False);
            Assert.That(RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.ZProbeAsEndstop }, 0, out _), Is.False);
            Assert.That(RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.InputPin }, 0, out _), Is.False, "no port named");
        });
    }

    [Test]
    public void ARefusedEndstopYieldsTheSentinel()
    {
        // The caller writes the result into the move either way, so a refusal has to be the value
        // that means "watch nothing" rather than a stale one
        RemoteEndstops.TryGetStopInput(new Endstop { Type = EndstopType.MotorStallAny }, 0, out uint stopInput);
        Assert.That(stopInput, Is.EqualTo(DuetControlServer.Motion.Native.MoveParams.NoStopInput));
    }
}
