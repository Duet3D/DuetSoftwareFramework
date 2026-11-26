using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.4, torque mode: the driver holds a commanded torque instead of a position, up to a speed limit
/// </summary>
/// <remarks>
/// RRF CanInterface::ConfigureRemoteDriver case 4 sends it only to a board that reports closed loop support,
/// and Duet3Expansion's ClosedLoop::ProcessM569Point4 decides whether the driver can take it. The torque is a
/// state of the driver, like its enable, so nothing is recorded
/// </remarks>
[TestFixture]
[GCode("M569.4")]
public class M569Point4Tests : BenchFixture
{
    /// <summary>
    /// T and V reach a closed loop board in their own fields
    /// </summary>
    /// <remarks><c>m569-4-torque-mode.yaml</c> and <c>m569-4-torque-mode-speed-limit.yaml</c></remarks>
    [Test]
    public async Task M569Point4CommandsATorque()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        await bench.ReportClosedLoopAsync(DriversRig.ClosedLoopBoard);
        const byte board = DriversRig.ClosedLoopBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569.4 P{Expansion.Driver(board, 0)} T0.001 V50"), Reply.IsEmpty,
                    "the board took the torque");

        (byte sentTo, CanMessageM569Point4 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point4>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(sent.P, Is.EqualTo(0));
            Assert.That(sent.T, Is.EqualTo(0.001f).Within(1e-6), "T is the commanded torque");
            Assert.That(sent.V, Is.EqualTo(50.0f).Within(1e-3), "V is the maximum speed in full steps per second");
        });

        await bench.Host.ExecuteCodeAsync($"M569.4 P{Expansion.Driver(board, 0)} T0");
        (_, CanMessageM569Point4 leave) = bench.CanMaster.LastCanMessage<CanMessageM569Point4>();
        Assert.Multiple(() =>
        {
            Assert.That(leave.T, Is.EqualTo(0.0f), "T0 is sent, which takes the driver out of torque mode");
            Assert.That(leave.V, Is.Null);
        });
    }

    /// <summary>
    /// What the board refuses about M569.4 comes back as an error naming the sub-code
    /// </summary>
    /// <remarks>
    /// <c>m569-4-error-missing-t.yaml</c>: unlike P, T is not checked on this side but by the board, so this is a
    /// whole error round trip. The others are the board's remaining refusals
    /// </remarks>
    [TestCase("", "missing T parameter")]
    [TestCase(" T0.5", "torque mode not available when driver is in open loop mode, has not been tuned, or is being tuned")]
    [TestCase(" T0.5", "cannot enter torque mode while moving")]
    public async Task M569Point4ReportsTheBoardsRefusal(string parameters, string refusal)
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point4>(refusal, CodeResult.Error))
                                             .StartAsync();
        await bench.ReportClosedLoopAsync(DriversRig.ClosedLoopBoard);

        string reply = await bench.Host.ExecuteCodeAsync($"M569.4 P{DriversRig.ClosedLoopDriver(0)}{parameters}");
        Assert.That(reply, Reply.IsError(refusal));
    }

    /// <summary>
    /// A board that has not reported closed loop support is not asked at all
    /// </summary>
    /// <remarks>RRF CanInterface.cpp ConfigureRemoteDriver case 4: "not supported by this driver" unless <c>hasClosedLoop</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point4OnABoardWithoutClosedLoopIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M569.4 P{DriversRig.MainDriver(0)} T0.5");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError("not supported by this driver"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point4>(), Is.Empty);
        });
    }

    /// <summary>
    /// A list of drivers puts each into torque mode with its own driver number, stopping at the first refusal
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp ConfigureDriver loops over every driver for M569.4, but each message takes its P from
    /// GCodeBuffer::GetDriverId, so every board is sent the first driver's number. Here each gets its own
    /// (rrf-differences.md section 6.1)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point4PutsEachDriverOfAListIntoTorqueMode()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        await bench.ReportClosedLoopAsync(DriversRig.ClosedLoopBoard);
        await bench.ReportClosedLoopAsync(DriversRig.MainDriverBoard);

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569.4 P{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)} T0.5"), Reply.IsEmpty);

        Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point4>().Select(sent => (sent.Board, sent.Message.P, sent.Message.T)),
                    Is.EquivalentTo(new (byte, byte?, float?)[] { (DriversRig.ClosedLoopBoard, 0, 0.5f), (DriversRig.MainDriverBoard, 3, 0.5f) }),
                    "each driver is sent its own number");
    }

    /// <summary>
    /// The first driver of a list that is refused stops the rest
    /// </summary>
    /// <remarks>RRF GCodes3.cpp ConfigureDriver breaks out of the loop on any result but ok</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point4StopsAtTheFirstRefusal()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point4>("cannot enter torque mode while moving", CodeResult.Error))
                                             .StartAsync();
        await bench.ReportClosedLoopAsync(DriversRig.ClosedLoopBoard);
        await bench.ReportClosedLoopAsync(DriversRig.MainDriverBoard);

        string reply = await bench.Host.ExecuteCodeAsync($"M569.4 P{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)} T0.5");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError("cannot enter torque mode while moving"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point4>(), Has.Count.EqualTo(1), "the second driver is not asked");
        });
    }

    /// <summary>
    /// M569.4 without P is refused before anything reaches the bus
    /// </summary>
    /// <remarks>RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c></remarks>
    [Test]
    public async Task M569Point4WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That(await bench.Host.ExecuteCodeAsync("M569.4 T0.5"), Reply.IsError(CodeErrors.MissingParameter('P')));
        Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point4>(), Is.Empty);
    }
}
