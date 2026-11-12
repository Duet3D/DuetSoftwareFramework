using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Can;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// Two EXP3HCs on one bus at addresses set by their switches. Each must be enumerated, configured and
/// moved on its own, with the traffic for one board going only to that board
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class MultipleExpansionBoardTests(Stack stack)
{
    private const int First = 2;
    private const int Second = 5;
    private const int StepsPerMm = 80;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [First] = BoardTypes.Exp3hc, [Second] = BoardTypes.Exp3hc },
            $"M564 H0 S0\nM584 X{First}.0 Y{Second}.0 Z{First}.2\nM92 X{StepsPerMm} Y{StepsPerMm} Z400\n",
            Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [SetUp]
    public void MarkCan() => _bench.Can.SetMark();

    [Test]
    public async Task EachBoardIsInTheObjectModelAtItsAddress()
    {
        JsonArray boards = (await _bench.GetObjectModelAsync("boards"))!.AsArray();

        Assert.That(boards.Select(b => b!["canAddress"]!.GetValue<int>()), Is.EqualTo(new[] { 0, First, Second }));
        Assert.That(boards.Skip(1).Select(b => b!["state"]!.GetValue<string>()), Is.All.EqualTo("running"));
        // Each has its own serial number
        Assert.That(boards[1]!["uniqueId"]!.GetValue<string>(), Is.Not.EqualTo(boards[2]!["uniqueId"]!.GetValue<string>()));
    }

    [Test]
    public void EachBoardAnnouncedItselfFromItsOwnAddress()
    {
        IEnumerable<byte> announcers = _bench.Can.All.Where(f => f.Is<CanMessageAnnounceV1>()).Select(f => f.Source).Distinct().Order();

        Assert.That(announcers, Is.EqualTo(new byte[] { First, Second }));
    }

    [Test]
    public async Task DriverConfigurationGoesOnlyToItsBoard()
    {
        await _bench.SendCodeAsync($"M569 P{Second}.0 S0");

        Assert.That(_bench.Can.Find<CanMessageM569>(source: 0, response: false).Select(f => f.Destination), Is.EqualTo(new byte[] { Second }));
        Assert.That((await _bench.GetObjectModelAsync("boards[1].drivers[0].config.direction"))!.GetValue<bool>(), Is.True);
        Assert.That((await _bench.GetObjectModelAsync("boards[2].drivers[0].config.direction"))!.GetValue<bool>(), Is.False);
        await _bench.SendCodeAsync($"M569 P{Second}.0 S1");
    }

    [Test]
    public async Task OneMoveStepsBothBoardsAtTheSameTime()
    {
        ulong firstBefore = await _bench.Io.StepCountAsync(First, 0);
        ulong secondBefore = await _bench.Io.StepCountAsync(Second, 0);

        await _bench.SendCodeAsync("G91");
        await _bench.SendCodeAsync("G1 X5 Y2.5 F600");
        await _bench.SendCodeAsync("M400");
        await _bench.SendCodeAsync("G90");

        Dictionary<byte, CanMessageMovementLinearShaped> moves = _bench.Can.Find<CanMessageMovementLinearShaped>(source: 0)
            .ToDictionary(f => f.Destination, f => f.As<CanMessageMovementLinearShaped>());
        Assert.That(moves.Keys.Order(), Is.EqualTo(new byte[] { First, Second }));
        Assert.That(moves[First].PerDrive[0].Steps, Is.EqualTo(5 * StepsPerMm));
        Assert.That(moves[Second].PerDrive[0].Steps, Is.EqualTo(2.5 * StepsPerMm));
        // Both start at the same time by the main board's clock
        Assert.That(moves[First].WhenToExecute, Is.EqualTo(moves[Second].WhenToExecute));
        Assert.That(await _bench.Io.StepCountAsync(First, 0) - firstBefore, Is.EqualTo(5 * StepsPerMm));
        Assert.That(await _bench.Io.StepCountAsync(Second, 0) - secondBefore, Is.EqualTo((ulong)(2.5 * StepsPerMm)));
    }

    [TestCase(First)]
    [TestCase(Second)]
    public async Task EachBoardAnswersForItself(int address)
    {
        CodeReply reply = await _bench.SendCodeAsync($"M115 B{address}");

        Assert.That(reply.Text, Does.StartWith("Duet EXP3HC"));
        IEnumerable<byte> repliers = _bench.Can.Find(CanMessageType.StandardReply, destination: 0, response: true).Select(f => f.Source).Distinct();
        Assert.That(repliers, Is.EqualTo(new[] { (byte)address }));
    }
}
