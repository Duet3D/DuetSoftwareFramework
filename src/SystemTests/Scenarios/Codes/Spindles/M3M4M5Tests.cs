using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Spindles;

/// <summary>M3, M4 and M5, driving a spindle</summary>
[TestFixture]
[GCode("M3", "M4", "M5")]
public class M3M4M5Tests : BenchFixture
{
    /// <summary>Spindle 0 on board 1, then CNC mode so M3/M4/M5 have spindle semantics</summary>
    private static readonly string SpindleCncConfig = $"""
        M950 R0 C"{Expansion.Out(Machines.DriverBoard, 6)}"
        M453
        """;

    /// <summary>
    /// In CNC mode M3 runs the spindle forward, M4 reverse, and M5 stops it; M3 without P and
    /// without a spindle tool is refused
    /// </summary>
    /// <remarks>
    /// The M3/M4/M5 cases in GCodes2.cpp: S sets the configured RPM (spindles[].active), the state
    /// change propagates the RPM to spindles[].current (Spindle::SetState and Spindle::SetRpm),
    /// and M5 zeroes current while active keeps the configured value
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task SpindleControlCodesDriveSpindleState()
    {
        await using Bench bench = await Bench.StartAsync(SpindleCncConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M3 P0 S5000");
        Spindle? spindle = await host.ReadModelAsync(model => model.Spindles[0]);
        Assert.Multiple(async () =>
        {
            Assert.That(spindle!.State, Is.EqualTo(SpindleState.Forward), "M3 sets spindles[0].state to forward (RRF GCodes2.cpp M3 case)");
            Assert.That(spindle!.Active, Is.EqualTo(5000), "M3 S sets spindles[0].active (RRF Spindle::SetConfiguredRpm)");
            Assert.That(spindle!.Current, Is.EqualTo(5000), "the state change applies the RPM to spindles[0].current (RRF Spindle::SetRpm)");
        });

        await host.ExecuteCodeAsync("M4 P0 S6000");
        spindle = await host.ReadModelAsync(model => model.Spindles[0]);
        Assert.Multiple(async () =>
        {
            Assert.That(spindle!.State, Is.EqualTo(SpindleState.Reverse), "M4 sets spindles[0].state to reverse (RRF GCodes2.cpp M4 case)");
            Assert.That(spindle!.Active, Is.EqualTo(6000), "M4 S sets spindles[0].active (RRF Spindle::SetConfiguredRpm)");
            Assert.That(spindle!.Current, Is.EqualTo(6000), "the state change applies the RPM to spindles[0].current (RRF Spindle::SetRpm)");
        });

        await host.ExecuteCodeAsync("M5");
        spindle = await host.ReadModelAsync(model => model.Spindles[0]);
        Assert.Multiple(async () =>
        {
            Assert.That(spindle!.State, Is.EqualTo(SpindleState.Stopped), "M5 without P stops every configured spindle (RRF GCodes2.cpp M5 case)");
            Assert.That(spindle!.Current, Is.EqualTo(0), "a stopped spindle's current RPM is 0 (RRF Spindle::SetRpm)");
            Assert.That(spindle!.Active, Is.EqualTo(6000), "M5 keeps the configured RPM in spindles[0].active (RRF Spindle::SetState)");
        });

        string reply = (await host.ExecuteCodeAsync("M3 S5000")).Trim();
        Assert.That(reply, Does.Contain("No P parameter and no active tool with spindle"),
                    "M3 without P and without a spindle tool is refused (RRF GCodes2.cpp M3 case)");
    }

    /// <summary>
    /// A tool with a spindle: selecting it stops the spindle, M3 S drives it and records the RPM
    /// on the tool, M568 F changes the RPM, and deselecting stops the spindle again
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool R attaches the spindle; Tool::Activate and Tool::Standby stop it on
    /// select and deselect (Tool.cpp, following NIST M6); the M3 case calls Tool::SetSpindleRpm
    /// for the current tool, which sets tools[].spindleRpm and the spindle's configured RPM; M568 F
    /// goes through the same Tool::SetSpindleRpm (GCodes::SetOrReportOffsets code 568)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task SpindleToolSelectionAndRpm()
    {
        await using Bench bench = await Bench.StartAsync(SpindleCncConfig + "\nM563 P0 S\"Cutter\" R0\n");
        DcsTestHost host = bench.Host;

        Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Spindle), Is.EqualTo(0), "M563 R0 sets tools[0].spindle (RRF GCodes::ManageTool)");

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.State), Is.EqualTo(SpindleState.Stopped), "selecting a spindle tool stops the spindle (RRF Tool::Activate)");
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.Active), Is.EqualTo(0), "selecting restores the tool's configured RPM, still 0 (RRF Tool::Activate)");
        });

        await host.ExecuteCodeAsync("M3 S5000");
        Spindle? spindle = await host.ReadModelAsync(model => model.Spindles[0]);
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.SpindleRpm), Is.EqualTo(5000), "M3 S on the current tool sets tools[0].spindleRpm (RRF Tool::SetSpindleRpm)");
            Assert.That(spindle!.Active, Is.EqualTo(5000), "the tool RPM reaches spindles[0].active (RRF Spindle::SetConfiguredRpm)");
            Assert.That(spindle!.State, Is.EqualTo(SpindleState.Forward), "M3 runs the spindle forward (RRF GCodes2.cpp M3 case)");
            Assert.That(spindle!.Current, Is.EqualTo(5000), "the running spindle turns at the set RPM (RRF Spindle::SetRpm)");
        });

        await host.ExecuteCodeAsync("M568 P0 F3000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.SpindleRpm), Is.EqualTo(3000), "M568 F sets tools[0].spindleRpm (RRF SetOrReportOffsets code 568)");
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.Active), Is.EqualTo(3000), "M568 F on the current tool updates spindles[0].active (RRF Tool::SetSpindleRpm)");
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.Current), Is.EqualTo(3000), "the running spindle follows the new RPM (RRF Spindle::SetConfiguredRpm with updateCurrentRpm)");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.State), Is.EqualTo(SpindleState.Stopped), "deselecting a spindle tool stops the spindle (RRF Tool::Standby)");
            Assert.That(await host.ReadModelAsync(model => model.Spindles[0]!.Current), Is.EqualTo(0), "a stopped spindle's current RPM is 0 (RRF Spindle::SetRpm)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.SpindleRpm), Is.EqualTo(3000), "the tool keeps its RPM for the next selection (RRF Tool::Standby)");
        });
    }
}
