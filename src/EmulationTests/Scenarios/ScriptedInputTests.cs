using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Traces;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// Inputs that change by themselves in emulated time, as the machine around a board would change them:
/// an endstop that triggers once its axis has moved so far, and a fan's tacho
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class ScriptedInputTests(Stack stack)
{
    private const int Address = 1;
    private const int StepsPerMm = 80;
    private const double MaxRpm = 6000;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [Address] = BoardTypes.Exp3hc },
            $"M564 H0 S0\nM584 X{Address}.0\nM92 X{StepsPerMm}\nM574 X1 S1 P\"{Address}.io0.in\"\n" +
            $"M950 F0 C\"{Address}.out3+out3.tach\"\nM950 F1 C\"{Address}.out4+out4.tach\"\n",
            Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [Test]
    public async Task AnEndstopThatTriggersPartWayStopsAHomingMove()
    {
        const int triggerAfterSteps = 25 * StepsPerMm;
        await AssertSuccessAsync("G91");
        await _bench.Io.DriveAsync(Address, "io0.in", false);
        await _bench.Io.DriveAfterStepsAsync(Address, 0, triggerAfterSteps, "io0.in", true);
        int inputsBefore = _bench.System.Io.Inputs.Count;

        // 100mm at 50mm/s would take 2s; the endstop is 25mm in
        TimeSpan start = _bench.System.Time;
        await AssertSuccessAsync("G1 H1 X-100 F3000");
        await AssertSuccessAsync("M400");

        InputEvent triggered = _bench.System.Io.Inputs.Skip(inputsBefore).Single();
        Assert.Multiple(async () =>
        {
            Assert.That(triggered.Value, Is.EqualTo("high"));
            Assert.That(triggered.Ports, Does.Contain("io0.in"));
            Assert.That(_bench.System.Time - start, Is.LessThan(TimeSpan.FromSeconds(1.5)), "the move stopped short");
            Assert.That((await _bench.GetObjectModelAsync("sensors.endstops[0].triggered"))!.GetValue<bool>(), Is.True);
            Assert.That((await _bench.GetObjectModelAsync("move.axes[0].homed"))!.GetValue<bool>(), Is.True);
        });
        await _bench.Io.ReleaseAsync(Address, "io0.in");
    }

    [Test]
    public async Task ATachoThatFollowsItsFanReportsTheFansSpeed()
    {
        await _bench.Io.TachoFollowsAsync(Address, "out3", "out3.tach", MaxRpm);

        await AssertSuccessAsync("M106 P0 S0.5");
        await WaitForRpmAsync(0, MaxRpm / 2);
        await AssertSuccessAsync("M106 P0 S1");
        await WaitForRpmAsync(0, MaxRpm);
        await AssertSuccessAsync("M106 P0 S0");
        await WaitForRpmAsync(0, 0);
    }

    [Test]
    public async Task APulseTrainIsReadAsItsSpeed()
    {
        // Two pulses a revolution, as RepRapFirmware assumes, so 100Hz is 3000 RPM
        await _bench.Io.PulsesAsync(Address, "out4.tach", 100);
        await WaitForRpmAsync(1, 3000);
        await _bench.Io.PulsesAsync(Address, "out4.tach", 0);
        await WaitForRpmAsync(1, 0);
    }

    private async Task WaitForRpmAsync(int fan, double rpm) =>
        await _bench.WaitUntilAsync(async () =>
        {
            double reported = (await _bench.GetObjectModelAsync($"fans[{fan}].rpm"))?.GetValue<double>() ?? -1;
            return Math.Abs(reported - rpm) <= Math.Max(30, rpm * 0.02);
        }, TimeSpan.FromSeconds(5), $"fan {fan} to report {rpm} RPM");

    private async Task AssertSuccessAsync(string code)
    {
        CodeReply reply = await _bench.SendCodeAsync(code, TimeSpan.FromSeconds(20));
        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), $"{code}: {reply}");
    }
}
