using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for what the main board's CAN address means here
/// </summary>
/// <remarks>
/// On a Duet 3 running RepRapFirmware, address 0 is the main board and carries drivers and IO. Here it
/// runs DuetCANMaster, which bridges SPI to CAN and drives nothing, so a code that addresses hardware
/// there is describing a machine that does not exist. Left unchecked it does not fail cleanly either:
/// the message goes out and the code sits out its CAN timeout before reporting nothing in particular
/// </remarks>
[TestFixture]
public class CanAddressesTests
{
    [Test]
    public void OnlyTheMainBoardHasNoHardware()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CanAddresses.HasNoHardware(CanId.MasterAddress), Is.True);
            Assert.That(CanAddresses.HasNoHardware(1), Is.False, "the first expansion board");
            Assert.That(CanAddresses.HasNoHardware(CanId.ToolBoardDefaultAddress), Is.False);
        });
    }

    [Test]
    public void TheAddressComesFromTheGeneratedDefinition()
    {
        // The addresses are CANlib's and are generated from it. Writing 0 here as well would be a
        // second place to change, and the two could disagree without anything noticing
        Assert.That(CanId.MasterAddress, Is.Zero);
    }

    [Test]
    public void TheReasonSaysWhatToDoInstead()
    {
        // A configuration written for RepRapFirmware names board 0 throughout, and a port written
        // without a board prefix means board 0 too, so "invalid" on its own would leave the user
        // guessing at what changed
        string message = CanAddresses.NoHardwareMessage("Driver 0.1");
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.StartWith("Driver 0.1"), "it names what was addressed");
            Assert.That(message, Does.Contain("DuetCANMaster"), "it says what is there instead");
            Assert.That(message, Does.Contain("expansion board"), "it says where the hardware lives");
        });
    }
}
