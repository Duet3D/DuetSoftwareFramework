using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// <para>
/// The M-codes that exist only to address the CAN bus itself, or a board across it: M655, M952,
/// M953 and M959. Written against the <c>testcases/can/</c> folder of
/// <see href="https://github.com/Duet3D/DuetRegressionTesting">DuetRegressionTesting</see>, one
/// scenario per case, so that the two suites are asserting the same behaviour from opposite ends.
/// </para>
/// <para>
/// What a code addressed to a board does is the message it puts on the bus, and mostly nothing else:
/// none of these has an object model field except M959, whose timeout is <c>boards[].timeout</c>.
/// The bench's controller acknowledges every CAN request with an empty standard reply rather than
/// composing a board's answer, so where RepRapFirmware's reply text comes back from the board -
/// M655's refusal, say - these assert the request and leave the text to the regression suite, which
/// has real boards to answer it.
/// </para>
/// </summary>
[TestFixture]
public class CanBusCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>CAN address of the expansion board the bench answers for</summary>
    private const byte Board = 1;

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
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
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
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
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
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M952 B{Board}");

        (byte board, CanMessageSetAddressAndNormalTiming sent) =
            bench.CanMaster.LastCanMessage<CanMessageSetAddressAndNormalTiming>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board), "the query goes to the board B named");
            Assert.That(sent.OldAddress, Is.EqualTo(Board), "which it also names inside the message");
            Assert.That(sent.DoSetTiming, Is.EqualTo(CanMessageSetAddressAndNormalTiming.DoSetTimingNo),
                        "and the timing is not to be set, which is the whole difference between asking and telling");
        });
    }

    /// <summary>
    /// A bare M953 asks the controller what the bus is running at, rather than answering from
    /// anything this side remembers
    /// </summary>
    /// <remarks>
    /// CanInterface::EnableCan's else branch reports when it saw no parameter, and reads the timing
    /// out of the CAN peripheral with can0dev-&gt;GetLocalCanTiming. The peripheral belongs to
    /// DuetCANMaster here, which answers the same query addressed to board 0: a
    /// setAddressAndNormalTiming with doSetTiming clear. Answering from a remembered value instead
    /// reported the defaults this side assumes rather than the timing the bus actually runs at, which
    /// on the bench is a different jump width. The regression case is
    /// <c>testcases/can/m953-report-can-timing.yaml</c>, and the reporting form is the only one of
    /// M953 safe to run on a bench: S, R, T, J and C change the arbitration rate on the main board
    /// alone, desynchronising it from every board on the bus with nothing able to change it back
    /// </remarks>
    [Test]
    public async Task M953AsksTheControllerForTheBusTiming()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M953");
        Assert.That(reply, Does.Not.StartWith("Error:"), "a bare M953 is a report, not an error");

        (byte board, CanMessageSetAddressAndNormalTiming query) =
            bench.CanMaster.LastCanMessage<CanMessageSetAddressAndNormalTiming>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(CanId.MasterAddress),
                        "the query goes to board 0, which is the controller driving the bus");
            Assert.That(query.OldAddress, Is.EqualTo(CanId.MasterAddress));
            Assert.That(query.DoSetTiming, Is.EqualTo(CanMessageSetAddressAndNormalTiming.DoSetTimingNo),
                        "and asks rather than tells, which is what makes it a report");
        });
    }

    /// <summary>
    /// M959 B refuses board 0, which is the main board and has no connection timeout to set
    /// </summary>
    /// <remarks>
    /// B is read with GetLimitedUIValue('B', 1, MaxCanAddress + 1), so 0 is below the range rather
    /// than merely absent. The regression case is
    /// <c>testcases/can/error-missing-parameters-can.yaml</c>
    /// </remarks>
    [Test]
    public async Task M959RefusesTheMainBoard()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync("M959 B0"), Does.StartWith("Error:"),
                    "board 0 is below M959's range (GetLimitedUIValue('B', 1, MaxCanAddress + 1))");
    }

    /// <summary>
    /// M959 T sets how long the main board waits before deciding a board has gone away, records it
    /// in boards[].timeout, and reports it back per board and for the whole bus
    /// </summary>
    /// <remarks>
    /// ExpansionManager::ConfigureConnectionTimeout (ExpansionManager.cpp) updates the main board's
    /// own copy and sends the board a setConnectionTimeout. This timeout is what raises the
    /// expansion-timeout and expansion-reconnect events, so getting it wrong changes when the
    /// machine notices a board has dropped off. The regression case is
    /// <c>testcases/can/m959-expansion-board-behaviour.yaml</c>, which is marked known-bad on the
    /// firmware side: no firmware handles the message yet, so the send times out while the readback
    /// still works from the main board's copy
    /// </remarks>
    [Test]
    public async Task M959SetsAndReportsAConnectionTimeout()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M959 B{Board} T30");

        (byte board, CanMessageM959 sent) =
            bench.CanMaster.LastCanMessage<CanMessageM959>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board), "the timeout goes to the board it is about");
            Assert.That(sent.T, Is.EqualTo(30), "carrying the value in seconds");
        });

        // boards[] is in discovery order with the address as a field, so it is matched on rather
        // than indexed
        Assert.That(await bench.Host.ReadModelAsync(
                        model => model.Boards.FirstOrDefault(b => b.CanAddress == Board)?.Timeout),
                    Is.EqualTo(30),
                    "and boards[].timeout records it on this side, which is where the main board's own "
                    + "copy lives (ExpansionManager.cpp ConfigureConnectionTimeout)");

        string one = await bench.Host.ExecuteCodeAsync($"M959 B{Board}");
        string all = await bench.Host.ExecuteCodeAsync("M959");
        Assert.Multiple(() =>
        {
            Assert.That(one, Does.Contain("Board 1 connection timeout 30 seconds"),
                        "M959 B1 reports that board's timeout from this side's copy, so it answers whether "
                        + "or not the board took the message");
            Assert.That(all, Does.Not.StartWith("Error:"),
                        "and a bare M959 enumerates the boards. It lists none here: the bench's controller "
                        + "answers CAN requests but announces no board, so the only entry is the one this "
                        + "code created, which is RepRapFirmware's state == unknown and is filtered out of "
                        + "the listing there too. The regression suite has announced boards to enumerate");
        });
    }

    /// <summary>
    /// M655 needs a board to send its request to, and says so before touching the bus
    /// </summary>
    /// <remarks>
    /// M655 exists for custom CAN-connected boards with features M950 and M42 cannot express, so
    /// exactly one of B (an address) or C (a port naming one) has to say where the request goes. The
    /// regression case is <c>testcases/can/m655-custom-can-request.yaml</c>, written as its two error
    /// paths because no rig has such a board
    /// </remarks>
    [Test]
    public async Task M655WithoutABoardOrPortIsRefused()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M655 P1");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "M655 with neither B nor C is an error");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM655>(), Is.Empty,
                        "and the check happens before anything reaches the bus");
        });
    }

    /// <summary>
    /// M655 packs the parameters it was given into the message it sends the board, one of each kind
    /// </summary>
    /// <remarks>
    /// The regression case is <c>testcases/can/m655-custom-can-request.yaml</c>, whose reference
    /// records P, R and E arriving at the board, which answers "not supported by this board" because
    /// no Duet board implements a custom feature. What is being asserted is the encoding: R is
    /// signed and E is a float, so a parameter landing in the wrong field would still produce a
    /// plausible-looking message
    /// </remarks>
    [Test]
    public async Task M655SendsItsParametersToTheBoard()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M655 B{Board} P1 R-4 E0.123");

        (byte board, CanMessageM655 sent) = bench.CanMaster.LastCanMessage<CanMessageM655>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board), "the request goes to the board B named");
            Assert.That(sent.P, Is.EqualTo(1), "P is carried as an unsigned value");
            Assert.That(sent.R, Is.EqualTo(-4), "R as a signed one, so a negative survives");
            Assert.That(sent.E, Is.EqualTo(0.123f).Within(1e-4), "and E as a float");
        });
    }

    /// <summary>
    /// M655's two string parameters: A rides in the message as written, C names the board and only
    /// the port goes with it
    /// </summary>
    /// <remarks>
    /// The regression case is <c>testcases/can/m655-string-parameters.yaml</c>. C is where the two
    /// differ: CanInterface::ProcessM655 takes the address off the front of it and sends the
    /// remainder, so <c>C"1.io0.in"</c> is a request to board 1 carrying <c>io0.in</c> - a board
    /// addresses its own ports and has no reader for an address
    /// </remarks>
    [Test]
    public async Task M655CarriesItsStringParameters()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M655 B{Board} A\"drt-string\"");
        (byte plainBoard, CanMessageM655 plain) = bench.CanMaster.LastCanMessage<CanMessageM655>();
        Assert.Multiple(() =>
        {
            Assert.That(plainBoard, Is.EqualTo(Board));
            Assert.That(plain.A, Is.EqualTo("drt-string"), "A is a plain string and travels as written");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M655 C\"{Board}.io0.in\"");
        (byte portBoard, CanMessageM655 port) = bench.CanMaster.LastCanMessage<CanMessageM655>();
        Assert.Multiple(() =>
        {
            Assert.That(portBoard, Is.EqualTo(Board), "C says which board the request goes to");
            Assert.That(port.C, Is.EqualTo("io0.in"),
                        "and the address comes off what it carries, as it does for every port name on the bus");
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
        await using JobBench bench = await JobControlBench.StartAsync();
        int sendsBefore = bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage).Count;

        string reply = await bench.Host.ExecuteCodeAsync($"M952 B{Board} S500");
        Assert.That(reply, Does.Not.Contain("Error"), "M952 B1 S500 accepts the new timing (RRF CanInterface::ChangeAddressAndNormalTiming)");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage).Count > sendsBefore,
                                             what: "M952's timing message leaving for the board");
    }

    /// <summary>
    /// M953 enables the CAN bus: a second enable reaches the controller and the code reports
    /// nothing.
    /// </summary>
    /// <remarks>
    /// RRF CanInterface::EnableCan enables the bus with the default data rate when no parameter
    /// changes the timing. There is no object model field for the bus state, so the observable is
    /// the enable crossing the link (config.g already sent the first one)
    /// </remarks>
    [Test]
    public async Task M953EnablesTheCanBus()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int enablesBefore = bench.CanMaster.SbcPackets(SbcRequest.EnableCAN).Count;

        string reply = await bench.Host.ExecuteCodeAsync("M953");
        Assert.That(reply, Does.Not.Contain("Error"), "M953 enables CAN without a report (RRF CanInterface::EnableCan)");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.SbcPackets(SbcRequest.EnableCAN).Count > enablesBefore,
                                             what: "M953's enable reaching the controller");
    }
}
