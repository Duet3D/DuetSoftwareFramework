using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.CanBus;

/// <summary>
/// M952, an expansion board's CAN timing: what is refused, what reaches the board, and every way the message
/// can fail on its way there or back
/// </summary>
[TestFixture]
[GCode("M952")]
public class M952Tests : BenchFixture
{
    /// <summary>
    /// M952 with no board number is refused before anything reaches the bus
    /// </summary>
    /// <remarks>
    /// CanInterface::ChangeAddressAndNormalTiming (CanInterface.cpp) opens with
    /// <c>gb.MustSee('B')</c>. The regression case is
    /// <c>testcases/can/error-missing-parameters-can.yaml</c>. Without the guard the code addresses
    /// board 0, which runs DuetCANMaster and answers nothing, so a mistyped M952 waits out the CAN
    /// response timeout and reports that instead of the mistake
    /// </remarks>
    [Test]
    public async Task M952WithoutABoardNumberIsRefused()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M952");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"),
                        "M952 with no B is an error (CanInterface.cpp ChangeAddressAndNormalTiming, MustSee)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetAddressAndNormalTiming>(), Is.Empty,
                        "and nothing goes to the bus for a code that named no board");
        });
    }

    /// <summary>
    /// M952 refuses a CAN address outside the range an address can be, rather than truncating it
    /// onto a board that does exist
    /// </summary>
    /// <remarks>
    /// The regression case is <c>testcases/can/m952-error-bad-address.yaml</c>, which uses 200 for
    /// both B and A on purpose: the target is an address no board can have, so even a firmware that
    /// stopped validating A could not move a real board. A CAN address is 7 bits, so 200 truncated
    /// to a byte is a different board entirely, which is what makes silently accepting it worse than
    /// refusing it
    /// </remarks>
    [Test]
    public async Task M952RefusesACanAddressOutOfRange()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M952 B200 A200");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "an address past the maximum is refused");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetAddressAndNormalTiming>(), Is.Empty,
                        "and no board is addressed, least of all whatever 200 truncates to");
        });
    }

    /// <summary>
    /// M952 with a board number and nothing else asks that board for its CAN timing without
    /// changing it
    /// </summary>
    /// <remarks>
    /// CanInterface::ChangeAddressAndNormalTiming sends CanMessageSetAddressAndNormalTiming with
    /// doSetTiming clear, which is what makes it a query: the board reports and ignores the timing
    /// in the message. The regression case is <c>testcases/can/m952-report-can-address.yaml</c>,
    /// which masks the timing field for that reason
    /// </remarks>
    [Test]
    public async Task M952ReportsABoardsCanTiming()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard}");

        (byte board, CanMessageSetAddressAndNormalTiming sent) =
            bench.CanMaster.LastCanMessage<CanMessageSetAddressAndNormalTiming>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the query goes to the board B named");
            Assert.That(sent.OldAddress, Is.EqualTo(Machines.DriverBoard), "which it also names inside the message");
            Assert.That(sent.DoSetTiming, Is.EqualTo(CanMessageSetAddressAndNormalTiming.DoSetTimingNo),
                        "and the timing is not to be set, which is the whole difference between asking and telling");
        });
    }

    /// <summary>
    /// M952 sends new CAN timing to an expansion board over the bus and reports nothing.
    /// </summary>
    /// <remarks>
    /// RRF CanInterface::ChangeAddressAndNormalTiming sends CanMessageSetAddressAndNormalTiming to
    /// the board named by B and replies ok. There is no object model field for the bus timing, so
    /// the observable is the CAN message leaving for the board
    /// </remarks>
    [Test]
    public async Task M952ConfiguresExpansionBoardCanTiming()
    {
        await using Bench bench = await Bench.StartAsync();
        int sendsBefore = bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage).Count;

        string reply = await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard} S500");
        Assert.That(reply, Does.Not.Contain("Error"), "M952 B1 S500 accepts the new timing (RRF CanInterface::ChangeAddressAndNormalTiming)");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage).Count > sendsBefore,
                                             what: "M952's timing message leaving for the board");
    }

    /// <summary>
    /// A CAN message that expects no reply fails its code when the controller says it could not send
    /// it, rather than reporting success.
    /// </summary>
    /// <remarks>
    /// The outcome of a send is the controller's <c>CanMessageSent</c> answer for the token, which is
    /// the only thing that knows whether the message reached the CAN peripheral. Resolving such a
    /// request earlier - when the transfer carrying it completes - reports success for a message the
    /// controller went on to refuse, because the refusal arrives in a later transfer. M952 B1 S500 is
    /// a <c>setAddressAndNormalTiming</c> sent with no reply expected, so it is the code that shows it
    /// </remarks>
    [Test]
    public async Task M952FailsWhenTheControllerCannotSendTheMessage()
    {
        await using Bench bench = await Bench.StartAsync();

        bench.CanMaster.ScriptCanSendStatus(CanStatus.BusError);
        Assert.That(async () => await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard} S500"),
                    Throws.Exception.With.Message.Contains("BusError"),
                    "a send the controller refused fails the code (LinkInterface.CompleteCanMessageSent)");
    }

    /// <summary>
    /// A CAN message that expects no reply completes once the controller has accepted it, and reports
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="M952FailsWhenTheControllerCannotSendTheMessage"/>: the same code on
    /// the same path must still finish normally when the controller answers <c>Ok</c>, so that routing
    /// the outcome through the acknowledgement does not leave a fire-and-forget message hanging until
    /// its timeout
    /// </remarks>
    [Test]
    public async Task M952CompletesOnTheControllerAcknowledgement()
    {
        await using Bench bench = await Bench.StartAsync();

        bench.CanMaster.ScriptCanSendStatus(CanStatus.Ok);
        string reply = await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard} S500", timeoutMs: 5_000);
        Assert.That(reply, Does.Not.Contain("Error"), "an accepted send completes the code without a reply from the bus");
    }

    /// <summary>
    /// A message that never got onto the wire reported as a timeout.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="M952ReportsABoardThatDoesNotAnswer"/>.
    /// 
    /// The behaviour may change in the future.
    /// </remarks>
    [Test]
    public async Task M952FailsWhenNothingOnTheBusAcknowledgedTheMessage()
    {
        await using Bench bench = await Bench.StartAsync();

        bench.CanMaster.ScriptCanSendStatus(CanStatus.DispatchTimeout);
        string reply = await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard}", timeoutMs: 5_000);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"),
                        "a board that did not answer is reported as an error, not thrown (CanResponse.FromTimeout)");
            Assert.That(reply, Does.Contain($"CAN response timeout: board {Machines.DriverBoard}"),
                        "and says so in RepRapFirmware's wording, which names the board rather than a result code");
        });
    }

    /// <summary>
    /// A reply the controller could not forward fails its code straight away, rather than leaving it
    /// to wait out a timeout for something that is not coming.
    /// </summary>
    /// <remarks>
    /// The controller's response ring is finite, and a reply it cannot take is lost: the message
    /// buffer is about to be reused and the bus has moved on. It answers the waiting token with
    /// <c>NoBuffer</c> for exactly that case, which is the same status it answers a request it could
    /// not register a reply mapping for, and either way the reply can never arrive
    /// </remarks>
    [Test]
    public async Task M952FailsWhenTheReplyCannotBeForwarded()
    {
        await using Bench bench = await Bench.StartAsync();

        bench.CanMaster.ScriptCanSendStatus(CanStatus.NoBuffer);
        Assert.That(async () => await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard}"),
                    Throws.Exception.With.Message.Contains("NoBuffer"),
                    "a reply that can never be forwarded fails the code now, not at the timeout");
    }

    /// <summary>
    /// A board that does not answer is reported to the code, not thrown at it.
    /// </summary>
    /// <remarks>
    /// The controller gives a board <c>CanInterface::UsualResponseTimeout</c> to reply and expires the
    /// request itself, reporting <c>Timeout</c>. That is the one acknowledgement status which is not a
    /// fault in the sending, so it has to resolve the request rather than fail it: RepRapFirmware
    /// returns <c>canResponseTimeout</c> from <c>SendRequestAndGetStandardReply</c> and reports it, and
    /// throwing would abandon every code after this one in a macro. M952 B1 with no timing expects a
    /// standard reply, so it is the code that shows it
    /// </remarks>
    [Test]
    public async Task M952ReportsABoardThatDoesNotAnswer()
    {
        await using Bench bench = await Bench.StartAsync();

        bench.CanMaster.ScriptCanSendStatus(CanStatus.ResponseTimeout);
        string reply = await bench.Host.ExecuteCodeAsync($"M952 B{Machines.DriverBoard}", timeoutMs: 5_000);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"),
                        "a board that did not answer is reported as an error, not thrown (CanResponse.FromTimeout)");
            Assert.That(reply, Does.Contain($"CAN response timeout: board {Machines.DriverBoard}"),
                        "and says so in RepRapFirmware's wording, which names the board rather than a result code");
        });
    }
}
