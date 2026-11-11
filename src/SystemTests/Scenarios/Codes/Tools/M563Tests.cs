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

/// <summary>M563, creating, redefining, deleting and reporting tools</summary>
[TestFixture]
[GCode("M563")]
public class M563Tests : BenchFixture
{
    /// <summary>
    /// M563 creates a tool whose number, name, heaters, extruders, fan map, mix, spindle and
    /// state all land in tools[]
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool and Tool::Create (GCodes.cpp, Tool.cpp): a new tool starts off with
    /// mix 1:0:0, no spindle, zero offsets, and active/standby temperatures of ABS_ZERO
    /// (-273.15 C); the reported fields are Tool's object model table (Tool.cpp)
    /// </remarks>
    [Test]
    public async Task M563CreatesToolWithMappedComponents()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        Tool? tool = await host.ReadModelAsync(model => model.Tools[0]);

        int toolCount = await host.ReadModelAsync(model => model.Tools.Count);
        int number = tool!.Number;
        string name = tool!.Name;
        int heaterCount = tool!.Heaters.Count;
        int heater0 = tool!.Heaters[0];
        int extruderCount = tool!.Extruders.Count;
        int extruder0 = tool!.Extruders[0];
        int fan0 = tool!.Fans[0];
        float mix0 = tool!.Mix[0];
        int spindle = tool!.Spindle;
        int spindleRpm = tool!.SpindleRpm;
        ToolState state = tool!.State;
        float offsetX = tool!.Offsets[0];
        float offsetY = tool!.Offsets[1];
        float active0 = tool!.Active[0];
        float standby0 = tool!.Standby[0];

        Assert.Multiple(() =>
        {
            Assert.That(toolCount, Is.EqualTo(1), "M563 P0 makes #tools report one tool (RRF Tool::AddTool, numToolsToReport)");
            Assert.That(number, Is.EqualTo(0), "M563 P0 sets tools[0].number (RRF Tool.cpp myNumber)");
            Assert.That(name, Is.EqualTo("Primary"), "M563 S sets tools[0].name (RRF GCodes::ManageTool)");
            Assert.That(heaterCount, Is.EqualTo(1), "M563 H0 gives tools[0].heaters one entry (RRF GCodes::ManageTool)");
            Assert.That(heater0, Is.EqualTo(0), "M563 H0 sets tools[0].heaters[0] (RRF GCodes::ManageTool)");
            Assert.That(extruderCount, Is.EqualTo(1), "M563 D0 gives tools[0].extruders one entry (RRF GCodes::ManageTool)");
            Assert.That(extruder0, Is.EqualTo(0), "M563 D0 sets tools[0].extruders[0] (RRF GCodes::ManageTool)");
            Assert.That(fan0, Is.EqualTo(0), "M563 F0 sets tools[0].fans to [0] (RRF GCodes::ManageTool fanMap)");
            Assert.That(mix0, Is.EqualTo(1).Within(1e-6), "a new tool's mix starts at 1 (RRF Tool::Create)");
            Assert.That(spindle, Is.EqualTo(-1), "no R parameter leaves tools[0].spindle at -1 (RRF Tool::Create)");
            Assert.That(spindleRpm, Is.EqualTo(0), "a new tool's spindleRpm is 0 (RRF Tool::Create)");
            Assert.That(state, Is.EqualTo(ToolState.Off), "a new tool's state is off (RRF Tool::Create ToolState::off)");
            Assert.That(offsetX, Is.EqualTo(0), "a new tool's X offset is 0 (RRF Tool::Create)");
            Assert.That(offsetY, Is.EqualTo(0), "a new tool's Y offset is 0 (RRF Tool::Create)");
            Assert.That(active0, Is.EqualTo(-273.15).Within(0.1), "a new tool's active temperature is ABS_ZERO (RRF Tool::Create)");
            Assert.That(standby0, Is.EqualTo(-273.15).Within(0.1), "a new tool's standby temperature is ABS_ZERO (RRF Tool::Create)");
        });
    }

    /// <summary>
    /// Redefining a tool with M563 replaces the whole definition: parameters not repeated are not
    /// carried over from the old tool
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool (GCodes.cpp): any parameter deletes the old tool and creates a new one
    /// from exactly the parameters given, so M563 P0 S"Renamed" leaves no drives and no heaters
    /// </remarks>
    /// TODO RRF currently doesn't allow a tool to be renamed and not drop the heaters, extruders, fans, etc.
    [Test]
    public async Task M563RedefineReplacesToolDefinition()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M563 P0 S\"Renamed\"");

        Tool? tool = await host.ReadModelAsync(model => model.Tools[0]);

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(1), "the redefined tool still exists (RRF GCodes::ManageTool)");
            Assert.That(tool!.Name, Is.EqualTo("Renamed"), "M563 S renames tools[0].name (RRF GCodes::ManageTool)");
            Assert.That(tool!.Heaters.Count, Is.EqualTo(0), "a redefinition without H drops tools[0].heaters (RRF GCodes::ManageTool recreates the tool)");
            Assert.That(tool!.Extruders.Count, Is.EqualTo(0), "a redefinition without D drops tools[0].extruders (RRF GCodes::ManageTool recreates the tool)");
        });
    }

    /// <summary>M563 P... D-1 H-1 deletes the tool, deselecting it first if it is current</summary>
    /// <remarks>
    /// GCodes::ManageTool (GCodes.cpp): a movement state whose current tool is the deleted number
    /// runs SelectTool(-1), then Tool::DeleteTool recomputes numToolsToReport, so #tools drops to 0
    /// </remarks>
    [Test]
    public async Task M563DeleteRemovesToolAndDeselects()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 selects the tool before the deletion");

        await host.ExecuteCodeAsync("M563 P0 D-1 H-1");

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(0), "M563 P0 D-1 H-1 removes the tool from tools[] (RRF Tool::DeleteTool)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "deleting the current tool deselects it (RRF GCodes::ManageTool SelectTool(-1))");
        });
    }

    /// <summary>A bare M563 P reports the tool in RepRapFirmware's format, matching the model</summary>
    /// <remarks>Tool::PrintTool (Tool.cpp): "Tool 0 - name: ...; drives: ...; ...; status: off"</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M563BareReportsToolConsistentWithModel()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        string reply = (await host.ExecuteCodeAsync("M563 P0")).Trim();
        string name = await host.ReadModelAsync(model => model.Tools[0]!.Name);

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "M563 P0 reports the tool number (RRF Tool::PrintTool)");
            Assert.That(reply, Does.Contain($"name: {name}"), "M563 P0 reports the model's tool name (RRF Tool::PrintTool)");
            Assert.That(reply, Does.Contain("drives: 0"), "M563 P0 reports the mapped drive (RRF Tool::PrintTool)");
            Assert.That(reply, Does.Contain("status: off"), "an unselected tool reports status off (RRF Tool::PrintTool)");
        });
    }
}
