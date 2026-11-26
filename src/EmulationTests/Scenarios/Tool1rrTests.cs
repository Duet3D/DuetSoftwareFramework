using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Can;
using DuetEmulation.Traces;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// A TOOL1RR, the single-driver tool board, at its default address beside an EXP3HC: its driver, its
/// thermistor, and a fan output and its tacho
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class Tool1rrTests(Stack stack)
{
    private static readonly int Address = BoardTypes.Tool1rr.DefaultAddress!.Value;
    private const int Exp3hcAddress = 1;
    private const int StepsPerMm = 400;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [Exp3hcAddress] = BoardTypes.Exp3hc, [Address] = BoardTypes.Tool1rr },
            $"M564 H0 S0\nM584 X{Exp3hcAddress}.0 U{Address}.0\nM92 X80 U{StepsPerMm}\n",
            Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [SetUp]
    public void Mark()
    {
        _bench.Can.SetMark();
        _bench.System.Io.SetMark();
    }

    [Test]
    public async Task TheBoardIsInTheObjectModelAtItsDefaultAddress()
    {
        JsonArray boards = (await _bench.GetObjectModelAsync("boards"))!.AsArray();

        JsonNode board = boards.Single(b => b!["canAddress"]!.GetValue<int>() == Address)!;
        Assert.That(board["shortName"]!.GetValue<string>(), Is.EqualTo("TOOL1RR"));
        Assert.That(board["state"]!.GetValue<string>(), Is.EqualTo("running"));
        Assert.That(board["drivers"]!.AsArray(), Has.Count.EqualTo(BoardTypes.Tool1rr.StepPins.Count));
        Assert.That(board["vIn"]!["current"]!.GetValue<double>(), Is.EqualTo(24.0).Within(0.2));

        CanMessageAnnounceV1 announce = _bench.Can.All.First(f => f.Is<CanMessageAnnounceV1>() && f.Source == Address).As<CanMessageAnnounceV1>();
        Assert.That(announce.BoardTypeAndFirmwareVersionString.Split('|')[0], Is.EqualTo("TOOL1RR"));
    }

    [Test]
    public async Task AMoveIsSteppedByItsDriver()
    {
        ulong before = await _bench.Io.StepCountAsync(Address, 0);

        await AssertSuccessAsync("G91");
        await AssertSuccessAsync("G1 U2 F300");
        await AssertSuccessAsync("M400");
        await AssertSuccessAsync("G90");

        Assert.That(await _bench.Io.StepCountAsync(Address, 0) - before, Is.EqualTo(2 * StepsPerMm));
    }

    [Test]
    public async Task ItsThermistorReadingReachesTheObjectModel()
    {
        await _bench.Io.SetThermistorAsync(Address, "temp0", 200);
        await AssertSuccessAsync($"M308 S6 P\"{Address}.temp0\" Y\"thermistor\" T100000 B4725");

        await _bench.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That((await _bench.GetObjectModelAsync("sensors.analog[6].lastReading"))!.GetValue<double>(), Is.EqualTo(200).Within(1));
    }

    [Test]
    public async Task AFanReportsTheSpeedOfItsTacho()
    {
        await AssertSuccessAsync($"M950 F2 C\"{Address}.out1+out1.tach\" Q250");
        await _bench.Io.TachoFollowsAsync(Address, "out1", "out1.tach", 6000);

        await AssertSuccessAsync("M106 P2 S0.5");
        await _bench.WaitUntilAsync(async () =>
            Math.Abs(((await _bench.GetObjectModelAsync("fans[2].rpm"))?.GetValue<double>() ?? -1) - 3000) <= 60,
            TimeSpan.FromSeconds(5), "fan 2 to report 3000 RPM");

        PinOutput output = await _bench.Io.OutputAsync(Address, "out1");
        Assert.That(output.Mode, Is.EqualTo(PinMode.Pwm), output.ToString());
        Assert.That(output.Duty, Is.EqualTo(0.5).Within(0.001));
        await AssertSuccessAsync("M106 P2 S0");
    }

    private async Task AssertSuccessAsync(string code)
    {
        CodeReply reply = await _bench.SendCodeAsync(code);
        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), $"{code}: {reply}");
    }
}
