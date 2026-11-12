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
/// An expansion board's pins as the bench sees them: the outputs its firmware drives, digital or PWM,
/// and the inputs the bench drives, read by polling or through the EIC
/// </summary>
[TestFixture(Stack.Rrf)]
[TestFixture(Stack.Dsf)]
[Category("Emulation")]
public class BoardIoTests(Stack stack)
{
    private const int Address = 1;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [Address] = BoardTypes.Exp3hc },
            $"M564 H0 S0\nM584 X{Address}.0 Y{Address}.1 Z{Address}.2\nM92 X80 Y80 Z80\n",
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
    public async Task AGpioOutputDrivesItsLevelsAndPwm()
    {
        await AssertSuccessAsync($"M950 P0 C\"{Address}.out0\" Q500");

        await AssertSuccessAsync("M42 P0 S1");
        Assert.That(await _bench.Io.OutputAsync(Address, "out0"), Is.EqualTo(new PinOutput(PinMode.High, 1, 0)));

        await AssertSuccessAsync("M42 P0 S0.25");
        PinOutput pwm = await _bench.Io.OutputAsync(Address, "out0");
        Assert.That(pwm.Mode, Is.EqualTo(PinMode.Pwm));
        Assert.That(pwm.Duty, Is.EqualTo(0.25).Within(0.001));
        Assert.That(pwm.Frequency, Is.EqualTo(500).Within(1));

        await AssertSuccessAsync("M42 P0 S0");
        Assert.That(await _bench.Io.OutputAsync(Address, "out0"), Is.EqualTo(new PinOutput(PinMode.Low, 0, 0)));

        // The trace has each change, in order
        IEnumerable<PinMode> modes = _bench.System.Io.Find(Address, "out0").Select(e => e.Output.Mode);
        Assert.That(modes, Is.SupersetOf(new[] { PinMode.High, PinMode.Pwm, PinMode.Low }), IoTrace.Dump(_bench.System.Io.SinceMark));
        Assert.That(_bench.System.Io.Find(Address, "out0").Last().Output.Mode, Is.EqualTo(PinMode.Low));
    }

    [Test]
    public async Task ATcOutputDrivesPwm()
    {
        // out1 is a TC output (tc3_1) where out0 is a TCC output
        await AssertSuccessAsync($"M950 P1 C\"{Address}.out1\" Q250");
        await AssertSuccessAsync("M42 P1 S0.6");

        PinOutput pwm = await _bench.Io.OutputAsync(Address, "out1");
        Assert.That(pwm.Mode, Is.EqualTo(PinMode.Pwm), pwm.ToString());
        Assert.That(pwm.Duty, Is.EqualTo(0.6).Within(0.001));
        Assert.That(pwm.Frequency, Is.EqualTo(250).Within(1));
        await AssertSuccessAsync("M42 P1 S0");
    }

    [Test]
    public async Task AFanOutputFollowsM106()
    {
        await AssertSuccessAsync($"M950 F1 C\"{Address}.out3\" Q250");
        await AssertSuccessAsync("M106 P1 S0.3");
        await _bench.WaitAsync(TimeSpan.FromMilliseconds(100));

        PinOutput fan = await _bench.Io.OutputAsync(Address, "out3");
        Assert.That(fan.Mode, Is.EqualTo(PinMode.Pwm), fan.ToString());
        Assert.That(fan.Duty, Is.EqualTo(0.3).Within(0.001));
        await AssertSuccessAsync("M106 P1 S0");
    }

    [Test]
    public async Task AGpioInputFollowsWhatDrivesIt()
    {
        KnownGap.On(stack, Stack.Dsf, "M950 J is not ported to DuetControlServer yet");
        await AssertSuccessAsync($"M950 J1 C\"{Address}.io1.in\"");

        await _bench.Io.DriveAsync(Address, "io1.in", true);
        await _bench.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.That((await _bench.GetObjectModelAsync("sensors.gpIn[1].value"))!.GetValue<int>(), Is.EqualTo(1));

        await _bench.Io.DriveAsync(Address, "io1.in", false);
        await _bench.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.That((await _bench.GetObjectModelAsync("sensors.gpIn[1].value"))!.GetValue<int>(), Is.EqualTo(0));
    }

    [Test]
    public async Task AHomingMoveStopsWhenItsEndstopTriggers()
    {
        await _bench.Io.DriveAsync(Address, "io2.in", false);
        await AssertSuccessAsync($"M574 X1 S1 P\"{Address}.io2.in\"");
        Assert.That((await _bench.GetObjectModelAsync("sensors.endstops[0].triggered"))!.GetValue<bool>(), Is.False);

        ulong before = await _bench.Io.StepCountAsync(Address, 0);
        await AssertSuccessAsync("G91");
        // A 100mm move at 10mm/s, which the endstop stops after about a second
        PendingCode move = await _bench.StartCodeAsync("G1 H1 X-100 F600");
        await _bench.WaitAsync(TimeSpan.FromSeconds(1));
        await _bench.Io.DriveAsync(Address, "io2.in", true);
        Assert.That((await move.WaitAsync()).Severity, Is.EqualTo(MessageType.Success));
        await AssertSuccessAsync("M400");
        await AssertSuccessAsync("G90");

        ulong steps = await _bench.Io.StepCountAsync(Address, 0) - before;
        Assert.That(steps, Is.GreaterThan(0).And.LessThan(100 * 80 / 2), "the move stopped well short of its end");
        Assert.That((await _bench.GetObjectModelAsync("sensors.endstops[0].triggered"))!.GetValue<bool>(), Is.True);

        await _bench.Io.DriveAsync(Address, "io2.in", false);
        await AssertSuccessAsync("M574 X0");
    }

    private async Task AssertSuccessAsync(string code)
    {
        CodeReply reply = await _bench.SendCodeAsync(code);
        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), $"{code}: {reply}");
    }
}
