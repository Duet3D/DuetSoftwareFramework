using System.Globalization;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>M302, cold extrusion</summary>
[TestFixture]
[GCode("M302")]
public class M302Tests : BenchFixture
{
    /// <summary>
    /// M302 controls cold extrusion: allowed reports both minimum temperatures as 0, forbidding
    /// restores the defaults, and S/R set the minimums the model reports
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 302 and Heat.cpp's objectModelTable: coldExtrudeTemperature
    /// and coldRetractTemperature report 0 while cold extrusion is allowed, else the configured
    /// minimums, whose defaults are DefaultMinExtrusionTemperature (160) and
    /// DefaultMinRetractionTemperature (90) from RRF3Common.h. The bench starts with cold extrusion
    /// allowed
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M302ControlsColdExtrusion()
    {
        await using Bench bench = await Bench.StartAsync("M302 P1");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.ColdExtrudeTemperature), Is.EqualTo(0).Within(0.01),
                        "with M302 P1 heat.coldExtrudeTemperature reports 0 (RRF Heat.cpp objectModelTable)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.ColdRetractTemperature), Is.EqualTo(0).Within(0.01),
                        "with M302 P1 heat.coldRetractTemperature reports 0 (RRF Heat.cpp objectModelTable)");
        });

        await bench.Host.ExecuteCodeAsync("M302 P0");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.ColdExtrudeTemperature), Is.EqualTo(160).Within(0.01),
                        "M302 P0 restores heat.coldExtrudeTemperature to DefaultMinExtrusionTemperature (RRF GCodes2.cpp case 302)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.ColdRetractTemperature), Is.EqualTo(90).Within(0.01),
                        "M302 P0 restores heat.coldRetractTemperature to DefaultMinRetractionTemperature (RRF GCodes2.cpp case 302)");
        });

        await bench.Host.ExecuteCodeAsync("M302 S120 R110");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.ColdExtrudeTemperature), Is.EqualTo(120).Within(0.01),
                        "M302 S sets heat.coldExtrudeTemperature (RRF Heat.cpp SetExtrusionMinTemp)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.ColdRetractTemperature), Is.EqualTo(110).Within(0.01),
                        "M302 R sets heat.coldRetractTemperature (RRF Heat.cpp SetRetractionMinTemp)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M302");
        Assert.That(report, Does.Contain("forbidden").And.Contain("120.0").And.Contain("110.0"),
                    "the M302 report matches the model's minimum temperatures (RRF GCodes2.cpp case 302)");
    }
}
