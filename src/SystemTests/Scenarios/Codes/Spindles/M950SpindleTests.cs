using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
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
    [Category("KnownGap")]
    [Test]
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
}
