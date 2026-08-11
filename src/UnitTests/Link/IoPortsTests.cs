using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// The grammar of a port name
/// </summary>
/// <remarks>
/// This is the one place the syntax is read, and the tests are here rather than beside either caller
/// for that reason. Two functions used to read it - one for endstops and probes, one for the generic
/// CAN messages - and they had drifted into different subsets of RepRapFirmware's
/// <c>IoPort::RemoveBoardAddress</c> without either being wrong on its own inputs
/// </remarks>
[TestFixture]
public class IoPortsTests
{
    [TestCase("1.io1.in", (byte)1, "io1.in")]
    [TestCase("0.io1.in", (byte)0, "io1.in")]
    [TestCase("121.io3.in", (byte)121, "io3.in")]
    [TestCase("126.out0", (byte)126, "out0", TestName = "TheHighestAddressIsAnAddress")]
    public void AnAddressBeforeADotIsAnAddress(string port, byte expectedBoard, string expectedLocal)
    {
        Assert.That(IoPorts.RemoveBoardAddress(port, out string local), Is.EqualTo(expectedBoard));
        Assert.That(local, Is.EqualTo(expectedLocal));
    }

    [TestCase("!1.io1.in", "!io1.in")]
    [TestCase("^1.io1.in", "^io1.in")]
    [TestCase("*1.io1.in", "*io1.in")]
    [TestCase("!^1.io1.in", "!^io1.in")]
    [TestCase("^!*1.io1.in", "^!*io1.in")]
    public void TheModifiersStayOnTheNameTheBoardIsGiven(string port, string expectedLocal)
    {
        // They say the pin is inverted or wants a pull-up, which is the board's business. Stripping
        // them along with the address would quietly turn a normally-closed switch into a
        // normally-open one - the machine would home by driving away from the endstop
        Assert.That(IoPorts.RemoveBoardAddress(port, out string local), Is.EqualTo(1));
        Assert.That(local, Is.EqualTo(expectedLocal));
    }

    [TestCase("out2", TestName = "NoAddressAtAll")]
    [TestCase("e0heat", TestName = "DigitsInsideTheNameAreNotAnAddress")]
    [TestCase("!io2.out", TestName = "ModifiedButUnaddressed")]
    [TestCase("io1.in", TestName = "ADotButNoDigits")]
    [TestCase("1x.io", TestName = "DigitsNotFollowedByADot")]
    [TestCase("127.out0", TestName = "PastTheHighestCanAddress")]
    [TestCase("999999999999.out0", TestName = "TooManyDigitsToBeAnAddress")]
    [TestCase("", TestName = "Empty")]
    [TestCase("!", TestName = "NothingButAModifier")]
    public void AnythingElseBelongsToTheLocalBoardUnchanged(string port)
    {
        // RepRapFirmware answers with the local board's own address for all of these, and leaves the
        // name alone. Here the local board is always the main board, which is what makes such a port
        // unusable - but that is the caller's rule to apply, not this one's
        Assert.That(IoPorts.RemoveBoardAddress(port, out string local), Is.EqualTo(CanId.MasterAddress));
        Assert.That(local, Is.EqualTo(port), "an unrecognised prefix is part of the name");
    }

    [Test]
    public void AnAddressWithNoPinLeavesNothing()
    {
        // Not an error here - callers that need a pin check for one. RepRapFirmware likewise strips
        // the address and lets the port assignment fail afterwards
        Assert.That(IoPorts.RemoveBoardAddress("3.", out string local), Is.EqualTo(3));
        Assert.That(local, Is.Empty);
    }
}
