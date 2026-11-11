using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>M567, a tool's mix ratio</summary>
[TestFixture]
[GCode("M567")]
public class M567Tests : BenchFixture
{
    /// <summary>M567 sets the tool's mix ratios and the bare form reports them</summary>
    /// <remarks>
    /// The M567 case in GCodes2.cpp: E values go to Tool::DefineMix (Tool.cpp), reported in
    /// tools[].mix; the report format is "Tool %d mix ratios: %.3f:%.3f"
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M567SetsAndReportsMixRatio()
    {
        const byte secondExtruderDriver = Machines.EDriver + 1;
        string mixConfig = $"""
            M569 P{Expansion.Driver(Machines.DriverBoard, Machines.EDriver)} S1
            M569 P{Expansion.Driver(Machines.DriverBoard, secondExtruderDriver)} S1
            M584 E{Expansion.Driver(Machines.DriverBoard, Machines.EDriver)}:{Expansion.Driver(Machines.DriverBoard, secondExtruderDriver)}
            M563 P0 S"Mixer" D0:1
            """;
        await using Bench bench = await Bench.StartAsync(mixConfig);
        DcsTestHost host = bench.Host;

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Mix[0]), Is.EqualTo(1).Within(1e-6), "the initial mix is 1:0 (RRF Tool::Create)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Mix[1]), Is.EqualTo(0).Within(1e-6), "the initial mix is 1:0 (RRF Tool::Create)");
        });

        await host.ExecuteCodeAsync("M567 P0 E0.6:0.4");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Mix[0]), Is.EqualTo(0.6).Within(1e-6), "M567 E sets tools[0].mix[0] (RRF Tool::DefineMix)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Mix[1]), Is.EqualTo(0.4).Within(1e-6), "M567 E sets tools[0].mix[1] (RRF Tool::DefineMix)");
        });

        string reply = (await host.ExecuteCodeAsync("M567 P0")).Trim();
        Assert.That(reply, Does.Contain("Tool 0 mix ratios: 0.600:0.400"), "bare M567 reports the mix (RRF GCodes2.cpp M567 case)");
    }
}
