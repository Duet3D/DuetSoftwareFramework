using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Spindles;
using DuetControlServer.Tools;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Tools.ToolSetup;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>M563, creating, redefining, deleting and reporting tools</summary>
[TestFixture]
[GCode("M563")]
public class M563Tests : BenchFixture
{
    /// <summary>The heater the scenarios give a tool, or the bed where a scenario needs one</summary>
    private const int HeaterNumber = 0;

    /// <summary>The spindle the scenarios configure, and one they do not</summary>
    private const int Spindle = 0, UnconfiguredSpindle = 1;

    /// <summary>A heater and a fan number nothing configures, which RRF still lets a tool name</summary>
    private const int UnconfiguredHeater = 3, UnconfiguredFan = 3;

    /// <summary>Driver of <see cref="Machines.DriverBoard"/> carrying the second extruder or the U axis</summary>
    private const byte SecondDriver = Machines.EDriver + 1;

    /// <summary>Index of the U axis once <see cref="UAxisConfig"/> adds it to <see cref="Machines.Xyz"/></summary>
    private const int UAxis = 3;

    /// <summary>Sensor and heater <see cref="HeaterNumber"/> on board 1, with no function until a code gives it one</summary>
    private static readonly string HeaterConfig = $"""
        M308 S{HeaterNumber} P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor"
        M950 H{HeaterNumber} C"{Expansion.Out(Machines.DriverBoard, 0)}" T{HeaterNumber}
        """;

    /// <summary>Two extruder drives, so that a tool can have several and L has a choice</summary>
    private static readonly string TwoExtruderConfig =
        $"M584 E{Expansion.Driver(Machines.DriverBoard, Machines.EDriver)}:{Expansion.Driver(Machines.DriverBoard, SecondDriver)}";

    /// <summary>A U axis to map X or Z onto, and fans 1 and 2 to map the tool's fan onto</summary>
    private static readonly string UAxisConfig = $"""
        M584 U{Expansion.Driver(Machines.DriverBoard, SecondDriver)}
        M950 F1 C"{Expansion.Out(Machines.DriverBoard, 3)}"
        M950 F2 C"{Expansion.Out(Machines.DriverBoard, 4)}"
        """;

    /// <summary>Spindle <see cref="Spindle"/> on board 1</summary>
    private static readonly string SpindleConfig = $"M950 R{Spindle} C\"{Expansion.Out(Machines.DriverBoard, 6)}\"";

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

    /// <summary>
    /// What M563 fills in for a parameter it is not given: X, Y and Z map straight through, fan 0
    /// is the print fan, the filament drive is the only drive, and retraction starts at its defaults
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool defaults xMap/yMap/zMap to DefaultX/Y/ZAxisMapping and fanMap to fan 0, and
    /// L to the sole drive when dCount is 1; Tool::Create sets DefaultRetractLength (2 mm) and
    /// DefaultRetractSpeed (1000 mm/min, reported in mm/s) and zeroes each heater's feedforward.
    /// Regression: m563-define-tool, whose tool 0 reports axes [[0],[1],[2]] and retraction 2.0/16.7
    /// </remarks>
    [Test]
    public async Task M563FillsInRrfDefaults()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, HeaterConfig, $"M563 P0 D0 H{HeaterNumber}");
        Tool tool = await bench.Host.ReadModelAsync(model => model.Tools[0]!);

        Assert.Multiple(() =>
        {
            Assert.That(tool.Axes, Is.EqualTo(new[] { new[] { 0 }, new[] { 1 }, new[] { 2 } }), "tools[0].axes maps X, Y and Z straight through (RRF DefaultX/Y/ZAxisMapping)");
            Assert.That(tool.Fans, Is.EqualTo(new[] { 0 }), "without F the tool's print fan is fan 0 (RRF ManageTool fanMap.SetBit(0))");
            Assert.That(tool.FilamentExtruder, Is.EqualTo(0), "without L a one-drive tool's filament drive is that drive (RRF ManageTool filamentDrive)");
            Assert.That(tool.FeedForwardPwm, Is.EqualTo(new[] { 0.0f }), "one zero feedforward PWM per heater (RRF Tool::Create)");
            Assert.That(tool.FeedForwardTemp, Is.EqualTo(new[] { 0.0f }), "one zero feedforward temperature per heater (RRF Tool::Create)");
            Assert.That(tool.Offsets, Has.Count.EqualTo(3), "one offset per visible axis (RRF Tool objectModelArrayTable offsets)");
            Assert.That(tool.IsRetracted, Is.False, "a new tool is not retracted (RRF Tool::Create)");
            Assert.That(tool.Retraction.Length, Is.EqualTo(2.0f).Within(1e-4), "tools[0].retraction.length is DefaultRetractLength (RRF Tool::Create)");
            Assert.That(tool.Retraction.Speed, Is.EqualTo(1000.0f / 60.0f).Within(0.05), "tools[0].retraction.speed is DefaultRetractSpeed in mm/s (RRF Tool::Create)");
            Assert.That(tool.Retraction.UnretractSpeed, Is.EqualTo(1000.0f / 60.0f).Within(0.05), "tools[0].retraction.unretractSpeed is DefaultRetractSpeed in mm/s (RRF Tool::Create)");
            Assert.That(tool.Retraction.ExtraRestart, Is.Zero, "tools[0].retraction.extraRestart starts at 0 (RRF Tool::Create)");
            Assert.That(tool.Retraction.ZHop, Is.Zero, "tools[0].retraction.zHop starts at 0 (RRF Tool::Create)");
        });
    }

    /// <summary>M563 without P is an error rather than a report of every tool</summary>
    /// <remarks>
    /// GCodes::ManageTool reads P with GetLimitedUIValue('P', MaxTools), which throws for a missing
    /// P before anything else is looked at. Regression: error-missing-parameters-tools ("M563" is an
    /// error)
    /// </remarks>
    [Test]
    public async Task M563WithoutPIsAnError()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"");

        string reply = (await bench.Host.ExecuteCodeAsync("M563")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "M563 without P is an error (RRF GetLimitedUIValue('P', MaxTools))");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(1), "tool 0 is untouched");
        });
    }

    /// <summary>M563 refuses a tool number outside 0 to MaxTools-1, and creates nothing</summary>
    /// <remarks>GCodes::ManageTool: GetLimitedUIValue('P', MaxTools) throws for an out-of-range P</remarks>
    [TestCase("M563 P-1 S\"Negative\"")]
    [TestCaseSource(nameof(ToolNumberPastMaximum))]
    public async Task M563RefusesOutOfRangeToolNumber(string code)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "the code is an error (RRF GetLimitedUIValue('P', MaxTools))");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count), Is.Zero, "no tool is created");
        });
    }

    private static readonly string[] ToolNumberPastMaximum = [$"M563 P{ToolManager.MaxTools} S\"Too high\""];

    /// <summary>M563 P with a tool number that has no tool is an error, and does not create one</summary>
    /// <remarks>
    /// GCodes::ManageTool: with nothing but P seen it only reports, and GetLockedTool finding nothing
    /// gives "Attempt to print details of non-existent tool" with GCodeResult::error
    /// </remarks>
    [Test]
    public async Task M563ReportOfAbsentToolIsAnError()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"");

        string reply = (await bench.Host.ExecuteCodeAsync("M563 P1")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "reporting an absent tool is an error (RRF GCodes::ManageTool)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(1), "M563 P1 does not create tool 1 (RRF GCodes::ManageTool reports only)");
        });
    }

    /// <summary>
    /// tools[] is indexed by tool number: a gap in the numbering is a null entry, and the array
    /// ends at the highest-numbered tool
    /// </summary>
    /// <remarks>
    /// RepRap objectModelArrayTable "tools" has Tool::GetNumToolsToReport() entries, each
    /// GetLockedTool(index), which is null where no tool has that number; Tool::DeleteTool
    /// recomputes numToolsToReport from the tools left
    /// </remarks>
    [Test]
    public async Task M563ToolNumberIndexesToolsArray()
    {
        await using Bench bench = await Bench.StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M563 P2 S\"Two\"");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(3), "tools[] reaches tool 2 (RRF numToolsToReport)");
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count > 0 ? model.Tools[0] : null), Is.Null, "tools[0] is null when there is no tool 0 (RRF GetLockedTool)");
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count > 2 ? model.Tools[2]?.Number : null), Is.EqualTo(2), "tool 2 is tools[2] (RRF GetLockedTool)");
        });

        await host.ExecuteCodeAsync("M563 P0 S\"Zero\"");
        await host.ExecuteCodeAsync("M563 P2 D-1 H-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(1), "deleting the highest tool shortens tools[] to the highest tool left (RRF Tool::DeleteTool)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]?.Number), Is.EqualTo(0), "tool 0 stays tools[0]");
        });
    }

    /// <summary>
    /// L picks a tool's filament drive and without L a multi-drive tool has none; a drive may be
    /// listed twice, and L-1 drops a one-drive tool's filament drive
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool takes filamentDrive from L, else the sole drive, else -1, and Tool::Create
    /// keeps it only if it is in 0..MaxExtruders-1 and does not check D for duplicates. Regression:
    /// m563-tool-with-axis-mapping (D0:1 L1 gives filamentExtruder 1). The wiki says the same of L
    /// </remarks>
    [TestCaseSource(nameof(DriveLists))]
    public async Task M563SetsFilamentDrive(string drives, int[] extruders, int filamentExtruder)
    {
        await using Bench bench = await Bench.StartAsync(TwoExtruderConfig);

        string reply = (await bench.Host.ExecuteCodeAsync($"M563 P0 {drives}")).Trim();
        Tool tool = await bench.Host.ReadModelAsync(model => model.Tools[0]!);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"), "the tool is accepted (RRF Tool::Create)");
            Assert.That(tool.Extruders, Is.EqualTo(extruders), "tools[0].extruders is D as given (RRF Tool::Create)");
            Assert.That(tool.FilamentExtruder, Is.EqualTo(filamentExtruder), "tools[0].filamentExtruder (RRF ManageTool filamentDrive)");
        });
    }

    private static readonly object[] DriveLists =
    [
        new object[] { "D0:1 L1", new[] { 0, 1 }, 1 },
        new object[] { "D0:1", new[] { 0, 1 }, -1 },
        new object[] { "D0:0", new[] { 0, 0 }, -1 },
        new object[] { "D0 L-1", new[] { 0 }, -1 },
    ];

    /// <summary>A multi-drive tool starts with the whole mix on its first drive</summary>
    /// <remarks>
    /// Tool::Create sets mix[0] to 1 and every other drive's to 0 (initial mix ratio 1:0:0).
    /// Regression: m563-tool-with-axis-mapping (D0:1 gives mix [1,0])
    /// </remarks>
    [TestCase("D0:1")]
    [TestCase("D0:0")]
    public async Task M563MultiDriveToolStartsWithWholeMixOnFirstDrive(string drives)
    {
        await using Bench bench = await Bench.StartAsync(TwoExtruderConfig);

        await bench.Host.ExecuteCodeAsync($"M563 P0 {drives}");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Tools[0]!.Mix.ToArray()), Is.EqualTo(new[] { 1.0f, 0.0f }).Within(1e-6),
                    "tools[0].mix is 1:0 (RRF Tool::Create)");
    }

    /// <summary>X, Y and Z map the tool's coordinates onto other axes, and F onto several fans</summary>
    /// <remarks>
    /// GCodes::ManageTool builds each axis map from the listed axis indices (X=0, Y=1, Z=2, U=3) and
    /// the fan map from F; tools[].axes reports the three maps. Regression:
    /// m563-tool-with-axis-mapping (X0:3 Y1 F1:2, then Z3)
    /// </remarks>
    [Test]
    public async Task M563MapsAxesAndFans()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxisConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync($"M563 P0 X0:{UAxis} Y1 F1:2");
        await host.ExecuteCodeAsync($"M563 P1 Z{UAxis}");

        Tool tool0 = await host.ReadModelAsync(model => model.Tools[0]!);
        Tool tool1 = await host.ReadModelAsync(model => model.Tools[1]!);
        Assert.Multiple(() =>
        {
            Assert.That(tool0.Axes, Is.EqualTo(new[] { new[] { 0, UAxis }, new[] { 1 }, new[] { 2 } }), "X0:3 maps X onto X and U (RRF ManageTool xMap)");
            Assert.That(tool0.Fans, Is.EqualTo(new[] { 1, 2 }), "F1:2 maps the print fan onto fans 1 and 2 (RRF ManageTool fanMap)");
            Assert.That(tool1.Axes, Is.EqualTo(new[] { new[] { 0 }, new[] { 1 }, new[] { UAxis } }), "Z3 maps Z onto U (RRF ManageTool zMap)");
        });
    }

    /// <summary>F-1 gives the tool no print fan</summary>
    /// <remarks>GCodes::ManageTool reads F as signed so that F-1 makes an empty fan map</remarks>
    [Test]
    public async Task M563FMinusOneMapsNoFans()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = (await bench.Host.ExecuteCodeAsync("M563 P0 F-1")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"), "F-1 is accepted (RRF ManageTool)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count > 0 ? model.Tools[0]?.Fans.ToArray() : null), Is.Empty, "tools[0].fans is empty (RRF FansBitmap::MakeFromArray)");
        });
    }

    /// <summary>
    /// A tool may name a heater or fan nothing has configured yet; nothing is sent for either
    /// </summary>
    /// <remarks>
    /// Tool::Create only checks the heater number is below MaxHeaters and not a bed or chamber
    /// heater, and ManageTool masks F to MaxFans; Heat::SetAsToolHeater does nothing for a heater
    /// FindHeater does not find
    /// </remarks>
    [Test]
    public async Task M563AcceptsUnconfiguredHeaterAndFan()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = (await bench.Host.ExecuteCodeAsync($"M563 P0 H{UnconfiguredHeater} F{UnconfiguredFan}")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"), "the tool is accepted (RRF Tool::Create)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count > 0 ? model.Tools[0]?.Heaters.ToArray() : null), Is.EqualTo(new[] { UnconfiguredHeater }), "tools[0].heaters names the heater (RRF Tool::Create)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count > 0 ? model.Tools[0]?.Fans.ToArray() : null), Is.EqualTo(new[] { UnconfiguredFan }), "tools[0].fans names the fan (RRF ManageTool fanMap)");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "no CAN message is sent for a heater that does not exist (RRF Heat::SetAsToolHeater)");
        });
    }

    /// <summary>M563 R attaches a configured spindle, and the R-1 deletion form removes the tool</summary>
    /// <remarks>
    /// GCodes::ManageTool passes R to Tool::Create, which requires the spindle to be configured;
    /// "M563 P# D-1 H-1 R-1" is the deletion form. Regression: m568-spindle-rpm (M563 P0 R0, torn
    /// down with M563 P0 R-1 D-1 H-1)
    /// </remarks>
    [Test]
    public async Task M563RAttachesSpindle()
    {
        await using Bench bench = await Bench.StartAsync(SpindleConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync($"M563 P0 R{Spindle} S\"Cutter\"");
        Tool tool = await host.ReadModelAsync(model => model.Tools[0]!);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Spindle, Is.EqualTo(Spindle), "tools[0].spindle is R (RRF Tool::Create)");
            Assert.That(tool.SpindleRpm, Is.Zero, "tools[0].spindleRpm starts at 0 (RRF Tool::Create)");
            Assert.That(tool.Extruders, Is.Empty, "a spindle tool needs no drives");
            Assert.That(tool.Heaters, Is.Empty, "a spindle tool needs no heaters");
        });

        await host.ExecuteCodeAsync("M563 P0 R-1 D-1 H-1");
        Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.Zero, "M563 P0 R-1 D-1 H-1 deletes the tool (RRF GCodes::ManageTool)");
    }

    /// <summary>The ways M563 replaces the selected spindle tool: deleting it, and redefining it</summary>
    private static readonly TestCaseData[] SpindleToolReplacements =
    [
        new TestCaseData("M563 P0 R-1 D-1 H-1", 0).SetArgDisplayNames("deleted"),
        new TestCaseData($"M563 P0 R{Spindle} S\"Renamed\"", 1).SetArgDisplayNames("redefined"),
    ];

    /// <summary>
    /// Deleting or redefining the selected tool stops its running spindle, which keeps its configured RPM
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool deselects the tool with SelectTool(-1), whose Tool::Standby sets the spindle's state to
    /// stopped (Tool.cpp, following NIST M6); Spindle::SetState leaves the configured RPM alone
    /// </remarks>
    [TestCaseSource(nameof(SpindleToolReplacements))]
    public async Task M563StopsTheSpindleOfTheSelectedTool(string code, int toolsLeft)
    {
        const int rpm = 5000;
        await using Bench bench = await Bench.StartAsync(SpindleConfig, $"M563 P0 R{Spindle}\nT0\nM3 S{rpm}");
        DcsTestHost host = bench.Host;
        Assert.That(await host.ReadModelAsync(model => model.Spindles[Spindle]!.State), Is.EqualTo(SpindleState.Forward),
                    "precondition: the selected tool's spindle is running");

        await host.ExecuteCodeAsync(code);
        Spindle spindle = await host.ReadModelAsync(model => model.Spindles[Spindle]!);
        Assert.Multiple(async () =>
        {
            Assert.That(spindle.State, Is.EqualTo(SpindleState.Stopped), "the spindle is stopped (RRF Tool::Standby)");
            Assert.That(spindle.Current, Is.Zero, "and turns at 0 rpm");
            Assert.That(spindle.Active, Is.EqualTo(rpm), "but keeps its configured RPM (RRF Spindle::SetState)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageWriteGpio>().Select(write => (write.Board, write.Message.PortNumber, write.Message.Pwm)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, (byte)SpindleManager.PortNumberFor(Spindle, 0), 0.0f) }),
                        "its PWM port is written to 0");
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(toolsLeft), "tools[] after the code");
        });
    }

    /// <summary>A definition RRF's Tool::Create or ManageTool rejects is an error and creates no tool</summary>
    /// <remarks>
    /// Tool::Create: "bad drive number" for a drive outside the extruders, "bad heater number" for a
    /// negative heater, "unconfigured spindle";
    /// ManageTool: "Cannot map two or more of X,Y,Z to the same axis". D-1 H-1 with R naming a
    /// spindle is not the deletion form, so it is a tool with drive -1
    /// </remarks>
    [TestCaseSource(nameof(InvalidDefinitions))]
    public Task M563RefusesInvalidDefinition(string code) => AssertRefusedAsync(code);

    /// <summary>A heater already assigned to the bed cannot also be a tool heater</summary>
    /// <remarks>Tool::Create: "heater %d is already assigned as a bed heater" for HeaterFunction::bed</remarks>
    [Test]
    public Task M563RefusesBedHeater() => AssertRefusedAsync($"M563 P0 H{HeaterNumber}");

    /// <summary>
    /// Run a definition against <see cref="Machines.Xye"/> with <see cref="HeaterNumber"/> as the bed
    /// and <see cref="Spindle"/> configured, and assert it is an error that creates no tool
    /// </summary>
    private static async Task AssertRefusedAsync(string code)
    {
        string config = $"""
            M140 H{HeaterNumber}
            {SpindleConfig}
            """;
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterConfig, config);

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "the definition is refused (RRF Tool::Create / GCodes::ManageTool)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count), Is.Zero, "no tool is created");
        });
    }

    private static readonly string[] InvalidDefinitions =
    [
        "M563 P0 D1",                           // Machines.Xye has extruder 0 only
        "M563 P0 D-1",                          // -1 is a drive only in the deletion form
        "M563 P0 H-1",
        "M563 P0 X0 Y0",
        $"M563 P0 R{UnconfiguredSpindle}",
        $"M563 P0 D-1 H-1 R{Spindle}",
    ];

    /// <summary>
    /// A redefinition that is refused still deletes the tool it was replacing: the tool is
    /// deselected, which puts its heaters on standby, then its heaters are switched off and it is
    /// removed
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool deselects and runs Tool::DeleteTool before Tool::Create validates the new
    /// definition, so when Create returns null the old tool is already gone. The deselection is
    /// MovementState::SelectTool(-1, false), which calls Tool::Standby: the heater is switched to
    /// the tool's standby temperature (commandOn, still ABS_ZERO) before Heat::SwitchOff sends
    /// commandOff at that same target
    /// </remarks>
    [Test]
    public async Task M563RefusedRedefinitionDeletesOldTool()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterConfig, $"M563 P0 D0 H{HeaterNumber} S\"Old\"\nT0");
        DcsTestHost host = bench.Host;
        Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 selects the tool before the redefinition");

        string reply = (await host.ExecuteCodeAsync("M563 P0 D1")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "drive 1 does not exist (RRF Tool::Create)");
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count), Is.Zero, "the old tool 0 was deleted before the new one was refused (RRF GCodes::ManageTool)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "the old tool was deselected (RRF GCodes::ManageTool SelectTool(-1))");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>().Select(m => (m.Board, (int)m.Message.HeaterNumber, m.Message.Command, m.Message.Function, m.Message.SetPoint)),
                        Is.EqualTo(new[]
                        {
                            (Machines.DriverBoard, HeaterNumber, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)HeaterFunction.Tool, HeatManager.AbsoluteZero),
                            (Machines.DriverBoard, HeaterNumber, CanMessageSetHeaterTemperatureV1.CommandOff, (byte)HeaterFunction.Tool, HeatManager.AbsoluteZero),
                        }),
                        "deselecting puts the old tool's heater on standby (RRF Tool::Standby), then it is switched off (RRF Tool::DeleteTool)");
        });
    }

    /// <summary>
    /// A redefinition refused while its parameters are read keeps the tool it was replacing, still
    /// selected and with nothing sent
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool returns "Cannot map two or more of X,Y,Z to the same axis" before it
    /// deselects or deletes anything, as a parameter it cannot read throws before then too
    /// </remarks>
    [Test]
    public async Task M563RefusedParametersKeepOldTool()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterConfig, $"M563 P0 D0 H{HeaterNumber} S\"Old\"\nT0");
        DcsTestHost host = bench.Host;

        string reply = (await host.ExecuteCodeAsync("M563 P0 X0 Y0")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "X and Y mapped to one axis is refused (RRF GCodes::ManageTool)");
            Assert.That(await host.ReadModelAsync(model => model.Tools.Count > 0 ? model.Tools[0]?.Name : null), Is.EqualTo("Old"), "tool 0 is kept (RRF GCodes::ManageTool)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "tool 0 stays selected");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>(), Is.Empty, "the heater is left alone");
        });
    }

    /// <summary>Redefining the selected tool deselects it, as deleting it does</summary>
    /// <remarks>
    /// GCodes::ManageTool runs SelectTool(-1) for every movement state whose current tool has the
    /// number, whether the code then recreates the tool or not
    /// </remarks>
    [Test]
    public async Task M563RedefiningSelectedToolDeselectsIt()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"Old\"\nT0");
        DcsTestHost host = bench.Host;
        Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 selects the tool before the redefinition");

        await host.ExecuteCodeAsync("M563 P0 S\"New\"");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "the redefined tool is no longer selected (RRF GCodes::ManageTool SelectTool(-1))");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Off), "the new tool 0 is off (RRF Tool::Create)");
        });
    }

    /// <summary>
    /// Giving a tool a heater asks the heater's board for the default tool model and sets the
    /// hot-end over-temperature monitor; a tool with no heater sends nothing
    /// </summary>
    /// <remarks>
    /// Tool::Create calls Heat::SetAsToolHeater, so Heater::SetFunction(tool) sends
    /// CanMessageSetDefaultHeaterModel (RemoteHeater::SetDefaultModel) and sets monitors[0] to
    /// DefaultHotEndTemperatureLimit. Regression: m563-define-tool (setDefaultHeaterModel heater 1,
    /// heaterFunction 0; heaters[1].max and monitors[0].limit 285; nothing for the bare tool 1)
    /// </remarks>
    [Test]
    public async Task M563ToolHeaterGetsDefaultToolModel()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M563 P1 S\"Bare tool\"");
        Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "a tool with no heater sends no CAN message (RRF Tool::Create)");

        await host.ExecuteCodeAsync($"M563 P0 D0 H{HeaterNumber}");
        (byte Board, CanMessageSetDefaultHeaterModel Message)[] defaults = [.. bench.CanMaster.CanMessages<CanMessageSetDefaultHeaterModel>()];
        Heater heater = await host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]!);
        Assert.Multiple(() =>
        {
            Assert.That(defaults.Select(m => (m.Board, (int)m.Message.Heater, m.Message.HeaterFunction)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, HeaterNumber, HeaterFunction.Tool) }),
                        "one setDefaultHeaterModel to the heater's board, for a tool heater (RRF RemoteHeater::SetDefaultModel)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>(), Is.Empty, "creating a tool switches no heater (RRF Tool::Create)");
            Assert.That(heater.Model.Enabled, Is.True, "heat.heaters[0].model.enabled (RRF FopDt::SetDefaultModel)");
            Assert.That(heater.Monitors[0]!.Sensor, Is.EqualTo(HeaterNumber), "monitors[0].sensor is the heater's sensor (RRF Heater::SetFunction)");
            Assert.That(heater.Monitors[0]!.Condition, Is.EqualTo(HeaterMonitorCondition.TooHigh), "monitors[0].condition (RRF Heater::SetFunction)");
            Assert.That(heater.Monitors[0]!.Limit, Is.EqualTo(HeatManager.DefaultHotEndTemperatureLimit), "monitors[0].limit is DefaultHotEndTemperatureLimit (RRF Heater::SetFunction)");
            Assert.That(heater.Monitors[0]!.Action, Is.EqualTo(HeaterMonitorAction.GenerateFault), "monitors[0].action (RRF Heater::SetFunction)");
            Assert.That(heater.Max, Is.EqualTo(HeatManager.DefaultHotEndTemperatureLimit), "max follows the monitor");
        });
    }

    /// <summary>
    /// Deleting or redefining a tool switches its heaters off on their board first
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool always runs Tool::DeleteTool, which calls Heat::SwitchOff for each of the
    /// old tool's heaters (RemoteHeater::SwitchOff: CanMessageSetHeaterTemperatureV1 commandOff).
    /// Regression: m563-delete-tool (setHeaterTemperatureV1 heater 1, command 1, function 0)
    /// </remarks>
    [TestCase("M563 P0 D-1 H-1", 0)]
    [TestCase("M563 P0 D-1 H-1 R-1", 0)]
    [TestCase("M563 P0 S\"Renamed\"", 1)]
    public async Task M563DeletionSwitchesToolHeatersOff(string code, int toolsLeft)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterConfig, $"M563 P0 D0 H{HeaterNumber}");

        await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>().Select(m => (m.Board, (int)m.Message.HeaterNumber, m.Message.Command, m.Message.Function)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, HeaterNumber, CanMessageSetHeaterTemperatureV1.CommandOff, (byte)HeaterFunction.Tool) }),
                        "the old tool's heater is switched off on its board (RRF Tool::DeleteTool, RemoteHeater::SwitchOff)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(toolsLeft), "tools[] after the code");
        });
    }

    /// <summary>Deleting a tool that does not exist succeeds and sends nothing</summary>
    /// <remarks>
    /// GCodes::ManageTool: Tool::DeleteTool finds nothing to delete and the deletion form creates
    /// nothing. The regression cases' teardowns rely on this ("deleting an already deleted tool")
    /// </remarks>
    [Test]
    public async Task M563DeletingAbsentToolSucceeds()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"");

        string reply = (await bench.Host.ExecuteCodeAsync("M563 P1 D-1 H-1")).Trim();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty, "the deletion succeeds silently (RRF GCodes::ManageTool)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Tools.Count), Is.EqualTo(1), "tool 0 is untouched");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty, "nothing is sent");
        });
    }
}
