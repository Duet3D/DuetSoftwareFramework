using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Tools.ToolSetup;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>G10, a tool's offsets and temperatures</summary>
[TestFixture]
[GCode("G10")]
public class G10Tests : BenchFixture
{
    /// <summary>G10 with axis letters sets the tool's offsets</summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 10 (GCodes.cpp): each seen axis letter goes to
    /// Tool::SetOffset, reported in tools[].offsets
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G10SetsToolOffsets()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G10 P0 X5 Y-2");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Offsets[0]), Is.EqualTo(5).Within(1e-6), "G10 X sets tools[0].offsets[0] (RRF Tool::SetOffset)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Offsets[1]), Is.EqualTo(-2).Within(1e-6), "G10 Y sets tools[0].offsets[1] (RRF Tool::SetOffset)");
        });

        string reply = (await host.ExecuteCodeAsync("G10 P0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain("Tool 0: offsets"), "bare G10 P reports the offsets (RRF SetOrReportOffsets)");
            Assert.That(reply, Does.Contain("X5.000"), "the reported X offset matches the model (RRF SetOrReportOffsets)");
            Assert.That(reply, Does.Contain("Y-2.000"), "the reported Y offset matches the model (RRF SetOrReportOffsets)");
        });
    }

    /// <summary>G10 with P/R/S sets the tool's temperatures, exactly as M568 does</summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 10 (GCodes.cpp): S and R share the temperature path
    /// with M568, so tools[].active/standby and the heater setpoints follow
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G10SetsToolTemperatures()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G10 P0 S200 R150");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Active[0]), Is.EqualTo(200), "G10 S sets tools[0].active (RRF SetOrReportOffsets code 10)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Standby[0]), Is.EqualTo(150), "G10 R sets tools[0].standby (RRF SetOrReportOffsets code 10)");
            Assert.That(await host.ReadModelAsync(model => model.Heat.Heaters[0]!.Active), Is.EqualTo(200), "the G10 temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
            Assert.That(await host.ReadModelAsync(model => model.Heat.Heaters[0]!.Standby), Is.EqualTo(150), "the G10 temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
        });
    }
}
