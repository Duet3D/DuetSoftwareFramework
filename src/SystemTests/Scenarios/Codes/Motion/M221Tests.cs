using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M221, the extrusion factor</summary>
[TestFixture]
[GCode("M221")]
public class M221Tests : BenchFixture
{
    /// <summary>
    /// M221 sets an extruder's extrusion factor, reported as a fraction in
    /// move.extruders[].factor; without D it needs a current tool
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 221 stores S * 0.01 via ChangeExtrusionFactor, Move.cpp
    /// reports it as move.extruders[].factor, the D report prints
    /// "Extrusion factor for extruder %u: %.1f%%", and with no D and no tool it answers
    /// "No tool selected"</remarks>
    [Test]
    public async Task M221SetsExtrusionFactor()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(1.0).Within(1e-3),
                    "the initial move.extruders[0].factor is 1.0 (RRF Move.cpp extruders factor)");

        await bench.Host.ExecuteCodeAsync("M221 S50 D0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(0.5).Within(1e-3),
                    "M221 S50 D0 sets move.extruders[0].factor to 0.5 (RRF GCodes2.cpp case 221)");

        string report = await bench.Host.ExecuteCodeAsync("M221 D0");
        Assert.That(report, Does.Contain("Extrusion factor for extruder 0: 50.0%"),
                    "M221 D0 reports the percentage consistent with move.extruders[0].factor (RRF GCodes2.cpp case 221)");

        string noTool = await bench.Host.ExecuteCodeAsync("M221 S120");
        Assert.That(noTool, Does.Contain("No tool selected"),
                    "M221 without D needs a current tool (RRF GCodes2.cpp case 221)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(0.5).Within(1e-3),
                    "a refused M221 leaves move.extruders[0].factor alone (RRF GCodes2.cpp case 221)");

        Assert.That(await bench.Host.ExecuteCodeAsync("M221 S120 D9"), Does.Contain("Invalid extruder number"),
                    "a D past the last extruder is refused (RRF GetLimitedUIValue against numExtruders)");
    }

    /// <summary>
    /// M221 with no D addresses every extruder the current tool collects, and reports them together
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 221: without D the factor goes through the current tool's
    /// IterateExtruders, and the report is "Extrusion factor(s) for current tool:" followed by one
    /// percentage per extruder. Only a machine with no tool at all answers "No tool selected"
    /// </remarks>
    [Test]
    public async Task M221WithoutDAddressesTheToolsExtruders()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, "M563 P0 D0");

        await bench.Host.ExecuteCodeAsync("T0");
        await bench.Host.ExecuteCodeAsync("M221 S75");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(0.75).Within(1e-3),
                    "M221 S75 with a tool selected reaches the tool's extruder (RRF Tool::IterateExtruders)");

        string report = await bench.Host.ExecuteCodeAsync("M221");
        Assert.That(report, Does.Contain("Extrusion factor(s) for current tool: 75.0%"),
                    "and a bare M221 reports them together (RRF GCodes2.cpp case 221)");
    }
}
