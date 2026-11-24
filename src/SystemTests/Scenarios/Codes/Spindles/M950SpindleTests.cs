using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using DuetControlServer.Spindles;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Spindles;

/// <summary>M950 R, creating a spindle</summary>
[TestFixture]
[GCode("M950")]
public class M950SpindleTests : BenchFixture
{
    /// <summary>
    /// M950 R creates a spindle with RepRapFirmware's default RPM range, L changes the range, and
    /// the bare form reports the spindle
    /// </summary>
    /// <remarks>
    /// Spindle::Configure (Spindle.cpp): a seen parameter moves the state from unconfigured to
    /// stopped; min/max default to DefaultMinSpindleRpm 60 and DefaultMaxSpindleRpm 10000
    /// (Configuration.h); the reported fields are Spindle's object model table
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950CreatesSpindleWithDefaultsAndLimits()
    {
        await using Bench bench = await Bench.StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync($"M950 R0 C\"{Expansion.Out(Machines.DriverBoard, 6)}\"");
        Spindle? spindle = await host.ReadModelAsync(model => model.Spindles[0]);
        Assert.Multiple(async () =>
        {
            Assert.That(spindle!.State, Is.EqualTo(SpindleState.Stopped), "M950 R moves the spindle to stopped (RRF Spindle::Configure)");
            Assert.That(spindle!.Active, Is.EqualTo(0), "a new spindle's configured RPM is 0 (RRF Spindle constructor)");
            Assert.That(spindle!.Current, Is.EqualTo(0), "a new spindle's current RPM is 0 (RRF Spindle constructor)");
            Assert.That(spindle!.Min, Is.EqualTo(60), "spindles[0].min defaults to DefaultMinSpindleRpm (RRF Configuration.h)");
            Assert.That(spindle!.Max, Is.EqualTo(10000), "spindles[0].max defaults to DefaultMaxSpindleRpm (RRF Configuration.h)");
        });

        await host.ExecuteCodeAsync("M950 R0 L4000:12000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.Min), Is.EqualTo(4000), "M950 L with two values sets spindles[0].min (RRF Spindle::Configure)");
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.Max), Is.EqualTo(12000), "M950 L with two values sets spindles[0].max (RRF Spindle::Configure)");
        });

        string reply = (await host.ExecuteCodeAsync("M950 R0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Spindle 0"), "bare M950 R reports the spindle (RRF Spindle::Configure)");
            Assert.That(reply, Does.Contain("rpm min 4000, max 12000"), "the reported range matches the model (RRF Spindle::Configure)");
        });
    }

    /// <summary>
    /// A board that creates a spindle's port but warns about it has its warning reported
    /// </summary>
    /// <remarks>
    /// A spindle is three general-purpose outputs driven together, so each port is created with the
    /// same M950 message a plain output is, and each reply carries a result code. The spindle is
    /// built either way when the board only warned
    /// </remarks>
    [Test]
    public async Task M950RReportsABoardWarningAboutTheSpindlePort()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.ReportWith<CanMessageM950Gpio>("out6 is shared with a fan", CodeResult.Warning);

        string reply = (await bench.Host.ExecuteCodeAsync($"M950 R0 C\"{Expansion.Out(Machines.DriverBoard, 6)}\"")).TrimEnd();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo("Warning: M950: out6 is shared with a fan"),
                        "the board took the port and warned about it, and the warning is the code's result");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[0]!.State),
                        Is.EqualTo(SpindleState.Stopped),
                        "a warning is not a refusal, so the spindle is still created");
        });
    }

    /// <summary>
    /// The spindle of the regression case: a PWM output and an on/off output, a frequency and an RPM
    /// range, read back by the bare form
    /// </summary>
    /// <remarks>
    /// Spindle::Configure. The regression reference m950-spindle-on-mainboard records
    /// <c>spindles[0].frequency</c> 100, <c>.min</c> 1000 and <c>.max</c> 12000, and the report
    /// <c>Spindle 0: type enaDir, enable pin out5, rpm pin out4 frequency 100Hz, rpm min 1000, max 12000</c>.
    /// Its ports are on the main board there; here every port is on an expansion board
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950RConfiguresAndReportsASpindle()
    {
        string pwmPort = Expansion.Out(Machines.DriverBoard, 4);
        string enablePort = Expansion.Out(Machines.DriverBoard, 5);
        await using Bench bench = await Bench.StartAsync();

        string created = await bench.Host.ExecuteCodeAsync($"M950 R0 C\"{pwmPort}+{enablePort}\" Q100 L1000:12000");
        Spindle? spindle = await bench.Host.ReadModelAsync(model => model.Spindles[0]);
        var sent = bench.CanMaster.CanMessages<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(created.Trim(), Is.Empty);
            Assert.That(spindle?.Frequency, Is.EqualTo(100), "Q sets spindles[0].frequency");
            Assert.That(spindle?.Min, Is.EqualTo(1000), "L's first value sets spindles[0].min");
            Assert.That(spindle?.Max, Is.EqualTo(12000), "and its second spindles[0].max");
            Assert.That(spindle?.Type, Is.EqualTo(SpindleType.EnaDir), "spindles[0].type defaults to enable/direction");
            Assert.That(spindle?.CanReverse, Is.False, "with no direction port it cannot reverse");
            Assert.That(spindle?.State, Is.EqualTo(SpindleState.Stopped));
            Assert.That(sent.Select(m => m.Message.C), Is.EqualTo(new[] { "out4", "out5" }),
                        "each port is claimed on its board as an output");
            Assert.That(sent[0].Message.Q, Is.EqualTo(100), "the PWM output at the spindle's frequency");
        });

        string report = (await bench.Host.ExecuteCodeAsync("M950 R0")).Trim();
        Assert.That(report, Is.EqualTo(
            $"Spindle 0: type enaDir, enable pin {enablePort}, rpm pin {pwmPort} frequency 100Hz, rpm min 1000, max 12000"));
    }

    /// <summary>
    /// The board address in front of the first port applies to the ports after it, as it does for
    /// every M950 that takes several ports, and a third port makes the spindle reversible
    /// </summary>
    [Test]
    [Category("KnownGap")]
    public async Task TheFirstPortsBoardAppliesToTheOthers()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M950 R1 C\"{Expansion.Out(Machines.DriverBoard, 4)}+out5+!out6\"");
        var sent = bench.CanMaster.CanMessages<CanMessageM950Gpio>();
        Assert.Multiple(async () =>
        {
            Assert.That(sent.Select(m => m.Board), Is.All.EqualTo(Machines.DriverBoard), "every port is on the first port's board");
            Assert.That(sent.Select(m => m.Message.C), Is.EqualTo(new[] { "out4", "out5", "!out6" }));
            Assert.That(sent.Select(m => (int?)m.Message.P), Is.EqualTo(new int?[]
            {
                SpindleManager.PortNumberFor(1, 0), SpindleManager.PortNumberFor(1, 1), SpindleManager.PortNumberFor(1, 2)
            }), "numbered from the top of the output range");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[1]!.CanReverse), Is.True,
                        "a direction port makes the spindle reversible");
        });
    }

    /// <summary>
    /// K sets the PWM range in its three forms: the maximum alone, minimum and maximum, and those with
    /// the idle PWM
    /// </summary>
    /// <remarks>Spindle::Configure, the K branch, which clamps each value to 0..1</remarks>
    [TestCase("K0.8", 0.0f, 0.8f, 0.0f)]
    [TestCase("K0.2:0.9", 0.2f, 0.9f, 0.0f)]
    [TestCase("K0.2:0.9:0.1", 0.2f, 0.9f, 0.1f)]
    [TestCase("K-1:2", 0.0f, 1.0f, 0.0f)]
    [Category("KnownGap")]
    public async Task M950KSetsThePwmRange(string k, float minPwm, float maxPwm, float idlePwm)
    {
        await using Bench bench = await Bench.StartAsync($"M950 R0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");

        await bench.Host.ExecuteCodeAsync($"M950 R0 {k}");
        Spindle? spindle = await bench.Host.ReadModelAsync(model => model.Spindles[0]);
        Assert.Multiple(() =>
        {
            Assert.That(spindle?.MinPwm, Is.EqualTo(minPwm).Within(1e-4));
            Assert.That(spindle?.MaxPwm, Is.EqualTo(maxPwm).Within(1e-4));
            Assert.That(spindle?.IdlePwm, Is.EqualTo(idlePwm).Within(1e-4));
        });
    }

    /// <summary>L with one value sets the maximum RPM and puts the minimum back to its default</summary>
    /// <remarks>Spindle::Configure: <c>minRpm = DefaultMinSpindleRpm; maxRpm = rpm[0]</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950LWithOneValueSetsTheMaximum()
    {
        await using Bench bench = await Bench.StartAsync(
            $"M950 R0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\" L1000:12000");

        await bench.Host.ExecuteCodeAsync("M950 R0 L24000");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[0]!.Min), Is.EqualTo(60));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[0]!.Max), Is.EqualTo(24000));
        });
    }

    /// <summary>
    /// T1 makes a forward/reverse spindle, which the report names by its forward and reverse ports
    /// </summary>
    /// <remarks>Spindle::Configure: <c>GetLimitedUIValue('T', 2)</c>, and the report's "forward" and "reverse"</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950TSetsTheSpindleType()
    {
        string pwm = Expansion.Out(Machines.DriverBoard, 4);
        await using Bench bench = await Bench.StartAsync($"M950 R0 C\"{pwm}+out5+out6\" T1");

        string report = (await bench.Host.ExecuteCodeAsync("M950 R0")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[0]!.Type), Is.EqualTo(SpindleType.FwdRev));
            Assert.That(report, Does.Contain("type fwdRev, forward pin out5, reverse pin out6"));
            Assert.That(await bench.Host.ExecuteCodeAsync("M950 R0 T2"), Does.StartWith("Error:"));
        });
    }

    /// <summary>A spindle that has not been configured is reported as such</summary>
    [Test]
    public async Task AnUnconfiguredSpindleIsReportedAsSuch()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M950 R1")).Trim(), Is.EqualTo("Spindle 1 is not configured"));
    }

    /// <summary>A spindle number past MaxSpindles is refused before the port is looked at</summary>
    /// <remarks>
    /// Platform::ConfigurePort: <c>GetLimitedUIValue('R', MaxSpindles)</c>. The regression reference
    /// error-missing-parameters-spindle-laser records <c>Error: M950: parameter 'R' too high</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950RefusesASpindleNumberPastTheLimit()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M950 R{SpindleManager.MaxSpindles} C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Gpio>(), Is.Empty, "and no pin is claimed");
        });
    }

    /// <summary>
    /// A port the board refuses leaves none of the spindle's ports claimed
    /// </summary>
    /// <remarks>IoPort::AssignPorts claims all of the ports or none of them</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ARefusedSpindlePortReleasesTheOthers()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.AnswerWith<CanMessageM950Gpio>(message => message.C == "out5"
            ? (CodeResult.Error, "Pin 1.out5 is not free")
            : (CodeResult.Ok, string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync($"M950 R0 C\"{Expansion.Out(Machines.DriverBoard, 4)}+out5\"");
        var sent = bench.CanMaster.CanMessages<CanMessageM950Gpio>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("is not free"));
            Assert.That(sent.Select(m => m.Message.C), Is.EqualTo(new[] { "out4", "out5", "nil" }),
                        "the first port is released again once the second is refused");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut.Count(port => port is not null)),
                        Is.Zero, "and no output is left behind");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles.ElementAtOrDefault(0)?.State ?? SpindleState.Unconfigured),
                        Is.EqualTo(SpindleState.Unconfigured), "the spindle is not configured");
        });
    }

    /// <summary>
    /// C"nil" releases a spindle's ports and leaves it configured with none
    /// </summary>
    /// <remarks>
    /// Spindle::Configure assigns the ports with IoPort::AssignPorts, which releases them for "nil", and
    /// a seen C leaves the spindle stopped rather than unconfigured. The regression reference
    /// m950-spindle-on-mainboard shows the result: after its setup's <c>M950 R0 C"nil"</c>,
    /// spindles[0] reports frequency 0, min 60 and max 10000
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950RNilReleasesTheSpindlesPorts()
    {
        await using Bench bench = await Bench.StartAsync($"M950 R0 C\"{Expansion.Out(Machines.DriverBoard, 4)}+out5\"");

        string reply = await bench.Host.ExecuteCodeAsync("M950 R0 C\"nil\"");
        var sent = bench.CanMaster.CanMessages<CanMessageM950Gpio>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(sent.Select(m => m.Message.C), Is.EqualTo(new[] { "nil", "nil" }), "both ports are released");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[0]!.Port), Is.Null, "spindles[0].port is empty");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Spindles[0]!.State), Is.EqualTo(SpindleState.Stopped));
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut.Count(port => port is not null)), Is.Zero);
        });
    }
}
