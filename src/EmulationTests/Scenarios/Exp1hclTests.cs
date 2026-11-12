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
/// An EXP1HCL, the single-driver closed-loop board, at its default address beside an EXP3HC, as the
/// regression bench has them, driven in open loop
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class Exp1hclTests(Stack stack)
{
    private static readonly int Address = BoardTypes.Exp1hcl.DefaultAddress!.Value;
    private const int Exp3hcAddress = 1;
    private const int StepsPerMm = 80;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [Exp3hcAddress] = BoardTypes.Exp3hc, [Address] = BoardTypes.Exp1hcl },
            $"M564 H0 S0\nM584 X{Address}.0 Y{Exp3hcAddress}.0 Z{Exp3hcAddress}.1\nM92 X{StepsPerMm} Y{StepsPerMm} Z{StepsPerMm}\n",
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
        Assert.That(board["shortName"]!.GetValue<string>(), Is.EqualTo("EXP1HCL"));
        Assert.That(board["state"]!.GetValue<string>(), Is.EqualTo("running"));
        Assert.That(board["drivers"]!.AsArray(), Has.Count.EqualTo(BoardTypes.Exp1hcl.StepPins.Count));
        Assert.That(board["vIn"]!["current"]!.GetValue<double>(), Is.EqualTo(24.0).Within(0.2));

        CanMessageAnnounceV1 announce = _bench.Can.All.First(f => f.Is<CanMessageAnnounceV1>() && f.Source == Address).As<CanMessageAnnounceV1>();
        Assert.That(announce.BoardTypeAndFirmwareVersionString.Split('|')[0], Is.EqualTo("EXP1HCL"));
    }

    [Test]
    public async Task AMoveIsSteppedByItsDriver()
    {
        ulong before = await _bench.Io.StepCountAsync(Address, 0);

        await _bench.SendCodeAsync("G91");
        await _bench.SendCodeAsync("G1 X5 Y5 F600");
        await _bench.SendCodeAsync("M400");
        await _bench.SendCodeAsync("G90");

        Assert.That(await _bench.Io.StepCountAsync(Address, 0) - before, Is.EqualTo(5 * StepsPerMm));
        Assert.That(_bench.Can.Find<CanMessageMovementLinearShaped>(source: 0).Select(f => f.Destination).Order(),
                    Is.EqualTo(new[] { (byte)Exp3hcAddress, (byte)Address }));
    }

    [Test]
    public async Task ItsThermistorReadingReachesTheObjectModel()
    {
        await _bench.Io.SetThermistorAsync(Address, "temp0", 60);
        CodeReply reply = await _bench.SendCodeAsync($"M308 S5 P\"{Address}.temp0\" Y\"thermistor\" T100000 B4725");
        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), reply.ToString());

        await _bench.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That((await _bench.GetObjectModelAsync("sensors.analog[5].lastReading"))!.GetValue<double>(), Is.EqualTo(60).Within(0.5));
    }

    [TestCase("out0", 0)]
    [TestCase("out1", 1)]
    public async Task AnOutputDrivesPwm(string port, int gpioPort)
    {
        // out0 is a TC output, out1 a TCC output on peripheral function G
        CodeReply reply = await _bench.SendCodeAsync($"M950 P{gpioPort} C\"{Address}.{port}\" Q1000");
        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), reply.ToString());
        await _bench.SendCodeAsync($"M42 P{gpioPort} S0.4");

        PinOutput output = await _bench.Io.OutputAsync(Address, port);
        Assert.That(output.Mode, Is.EqualTo(PinMode.Pwm), output.ToString());
        Assert.That(output.Duty, Is.EqualTo(0.4).Within(0.001));
        Assert.That(output.Frequency, Is.EqualTo(1000).Within(1));
        await _bench.SendCodeAsync($"M42 P{gpioPort} S0");
    }
}
