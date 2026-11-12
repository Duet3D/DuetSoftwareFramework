using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Can;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// An MB6HC with an EXP3HC at CAN address 1, booted through its
/// bootloader. Codes sent to the main board must produce the right CAN requests, the expansion board's
/// replies, and the right object model
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class ExpansionBoardTests(Stack stack)
{
    private const int Address = 1;
    private const int XyStepsPerMm = 80;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [Address] = BoardTypes.Exp3hc },
            $"M564 H0 S0\nM584 X{Address}.0 Y{Address}.1 Z{Address}.2\nM92 X{XyStepsPerMm} Y{XyStepsPerMm} Z400\n",
            Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [SetUp]
    public void MarkCan() => _bench.Can.SetMark();

    [Test]
    public async Task TheBoardIsInTheObjectModel()
    {
        JsonArray boards = (await _bench.GetObjectModelAsync("boards"))!.AsArray();

        JsonNode expansion = boards[1]!;
        Assert.That(expansion["canAddress"]!.GetValue<int>(), Is.EqualTo(Address));
        Assert.That(expansion["shortName"]!.GetValue<string>(), Is.EqualTo("EXP3HC"));
        Assert.That(expansion["state"]!.GetValue<string>(), Is.EqualTo("running"));
        Assert.That(expansion["firmwareFileName"]!.GetValue<string>(), Is.EqualTo("Duet3Firmware_EXP3HC.bin"));
        if (stack == Stack.Rrf)
        {
            // Under DSF the main board's version is DuetControlServer's
            Assert.That(expansion["firmwareVersion"]!.GetValue<string>(), Is.EqualTo(boards[0]!["firmwareVersion"]!.GetValue<string>()));
        }
        Assert.That(expansion["drivers"]!.AsArray(), Has.Count.EqualTo(BoardTypes.Exp3hc.StepPins.Count));
    }

    [Test]
    public void TheBoardAnnouncedItselfAndWasAcknowledged()
    {
        IReadOnlyList<CanFrame> frames = _bench.Can.All;
        CanFrame announce = frames.First(f => f.Is<CanMessageAnnounceV1>());
        Assert.That((announce.Source, announce.Destination), Is.EqualTo((Address, 0)));
        CanMessageAnnounceV1 message = announce.As<CanMessageAnnounceV1>();
        Assert.That(message.BoardTypeAndFirmwareVersionString.Split('|')[0], Is.EqualTo("EXP3HC"));
        Assert.That(message.NumDrivers, Is.EqualTo(BoardTypes.Exp3hc.StepPins.Count));

        CanFrame acknowledge = frames.First(f => f.Is<CanMessageAcknowledgeAnnounce>() && f.Time > announce.Time);
        Assert.That((acknowledge.Source, acknowledge.Destination), Is.EqualTo((0, Address)));
    }

    [Test]
    public async Task TheBoardReportsItsSuppliesAndTemperature()
    {
        JsonNode board = (await _bench.GetObjectModelAsync("boards[1]"))!;

        Assert.That(board["vIn"]!["current"]!.GetValue<double>(), Is.EqualTo(24.0).Within(0.2));
        Assert.That(board["v12"]!["current"]!.GetValue<double>(), Is.EqualTo(12.0).Within(0.2));
        Assert.That(board["mcuTemp"]!["current"]!.GetValue<double>(), Is.EqualTo(25.0).Within(1.0));
        Assert.That(board["uniqueId"]!.GetValue<string>(), Is.Not.Empty);
    }

    [Test]
    public async Task M115IsAnsweredByTheExpansionBoard()
    {
        CodeReply reply = await _bench.SendCodeAsync($"M115 B{Address}");

        CanFrame request = _bench.Can.Find(CanMessageType.ReturnInfo, source: 0, destination: Address, response: false).Single();
        StandardReply remote = await _bench.WaitForReplyAsync(request);
        Assert.That(reply.Text, Is.EqualTo(remote.Text));
        Assert.That(reply.Text, Does.StartWith("Duet EXP3HC rev 1.03 or later firmware version "));
    }

    [Test]
    public async Task M569ConfiguresTheRemoteDriver()
    {
        CodeReply reply = await _bench.SendCodeAsync($"M569 P{Address}.0 S0");

        Assert.That(reply.Text, Is.Empty);
        CanFrame request = _bench.Can.Find<CanMessageM569>(source: 0, destination: Address, response: false).Single();
        Assert.That(request.Parameters<CanMessageM569>(), Is.EquivalentTo(new Dictionary<char, object> { ['P'] = 0L, ['S'] = 0L }));
        Assert.That((await _bench.WaitForReplyAsync(request)).Result, Is.EqualTo(CodeResult.Ok));
        Assert.That((await _bench.GetObjectModelAsync("boards[1].drivers[0].config.direction"))!.GetValue<bool>(), Is.False);

        await _bench.SendCodeAsync($"M569 P{Address}.0 S1");
        Assert.That((await _bench.GetObjectModelAsync("boards[1].drivers[0].config.direction"))!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task M308CreatesARemoteSensorWhoseReadingReachesTheObjectModel()
    {
        await _bench.Io.SetThermistorAsync(Address, "temp2", 25.0);
        CodeReply reply = await _bench.SendCodeAsync($"M308 S3 P\"{Address}.temp2\" Y\"thermistor\" T100000 B4725");

        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), reply.ToString());
        CanFrame request = _bench.Can.Find<CanMessageM308V1>(source: 0, destination: Address).Single();
        // The board number is in the CAN address; the board sees only its own port name
        Assert.That(request.Parameters<CanMessageM308V1>()['P'], Is.EqualTo("temp2"));
        Assert.That((await _bench.WaitForReplyAsync(request)).Result, Is.EqualTo(CodeResult.Ok));

        await _bench.WaitAsync(System.TimeSpan.FromSeconds(1));
        Assert.That((await _bench.GetObjectModelAsync("sensors.analog[3].lastReading"))!.GetValue<double>(), Is.EqualTo(25.0).Within(0.5));

        await _bench.Io.SetThermistorAsync(Address, "temp2", 100.0);
        await _bench.WaitAsync(System.TimeSpan.FromSeconds(1));
        Assert.That((await _bench.GetObjectModelAsync("sensors.analog[3].lastReading"))!.GetValue<double>(), Is.EqualTo(100.0).Within(0.5));
        CanFrame report = _bench.Can.Find<CanMessageSensorTemperatures>(source: Address).Last();
        Assert.That(report.As<CanMessageSensorTemperatures>().WhichSensors & (1UL << 3), Is.Not.Zero);
    }

    [Test]
    public async Task AMoveIsSteppedByTheExpansionBoard()
    {
        ulong[] before = [.. await Task.WhenAll(Enumerable.Range(0, 3).Select(d => _bench.Io.StepCountAsync(Address, d)))];

        await _bench.SendCodeAsync("G91");
        await _bench.SendCodeAsync("G1 X10 Y-5 F600");
        await _bench.SendCodeAsync("M400");
        await _bench.SendCodeAsync("G90");

        CanMessageMovementLinearShaped move = _bench.Can.Find<CanMessageMovementLinearShaped>(source: 0, destination: Address).Single()
            .As<CanMessageMovementLinearShaped>();
        // X and Y are the board's drivers 0 and 1
        Assert.That(move.NumDrivers, Is.EqualTo(2));
        Assert.That(new[] { move.PerDrive[0].Steps, move.PerDrive[1].Steps }, Is.EqualTo(new[] { 10 * XyStepsPerMm, -5 * XyStepsPerMm }));

        ulong[] after = [.. await Task.WhenAll(Enumerable.Range(0, 3).Select(d => _bench.Io.StepCountAsync(Address, d)))];
        Assert.That(after.Zip(before, (a, b) => a - b), Is.EqualTo(new ulong[] { 10 * XyStepsPerMm, 5 * XyStepsPerMm, 0 }));
        Assert.That((await _bench.GetObjectModelAsync("move.axes[0].machinePosition"))!.GetValue<double>(), Is.EqualTo(10.0).Within(0.001));
        Assert.That((await _bench.GetObjectModelAsync("move.axes[1].machinePosition"))!.GetValue<double>(), Is.EqualTo(-5.0).Within(0.001));
    }

    [Test]
    public async Task TheClockSyncIsTight()
    {
        // The board reports its clock sync in M122: jitter in step clocks and how often it resynced
        CodeReply reply = await _bench.SendCodeAsync($"M122 B{Address}");

        string line = reply.Text.Split('\n').Single(l => l.StartsWith("Sync err accum"));
        string[] jitter = line.Split("peak jitter ")[1].Split(',')[0].Split('/');
        Assert.That(int.Parse(jitter[0]), Is.InRange(-20, 0), line);
        Assert.That(int.Parse(jitter[1]), Is.InRange(0, 20), line);
        Assert.That(line, Does.Contain("resyncs 0/0"));
    }
}
