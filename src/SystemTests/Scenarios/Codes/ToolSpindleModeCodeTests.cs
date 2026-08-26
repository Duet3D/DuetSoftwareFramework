using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// Tools, spindles and machine mode against the object model: T, M563, M567, M568, G10 (tool
/// offsets and temperatures), M3/M4/M5, M450-M453 and M950 R. Expected values come from
/// RepRapFirmware: GCodes::ManageTool and GCodes::SetOrReportOffsets (GCodes.cpp),
/// GCodes::HandleTcode and the M3/M4/M5 and M450-M453 cases (GCodes2.cpp), the toolChange0..2
/// states (GCodes4.cpp), Tool.cpp and Spindle.cpp for the reported fields
/// </summary>
[TestFixture]
public class ToolSpindleModeCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>
    /// Sensor 0 and heater 0 on board 1, a fan for the tool to map, and tool 0 using extruder
    /// drive 0, heater 0 and fan 0 (the shape of HeatingTests.HeatedBedConfig, minus M140)
    /// </summary>
    private const string HeaterToolConfig = """
        M308 S0 P"1.temp0" Y"thermistor"
        M950 H0 C"1.out0" T0
        M950 F0 C"1.out3"
        M563 P0 S"Primary" D0 H0 F0
        """;

    /// <summary>
    /// Two tools with no drives or heaters, and the globals the tool change macros record their
    /// run order in: each macro increments global.toolSeq and stores the value it drew
    /// </summary>
    private const string TwoBareToolsConfig = """
        M563 P0 S"Zero"
        M563 P1 S"One"
        global toolSeq = 0
        global tfree0At = 0
        global tpre0At = 0
        global tpost0At = 0
        global tfree1At = 0
        global tpre1At = 0
        global tpost1At = 0
        """;

    /// <summary>Spindle 0 on board 1, then CNC mode so M3/M4/M5 have spindle semantics</summary>
    private const string SpindleCncConfig = """
        M950 R0 C"1.out6"
        M453
        """;

    /// <summary>Write tfree/tpre/tpost for tools 0 and 1, each recording its order of execution</summary>
    private static void WriteToolChangeMacros(VirtualSd sd)
    {
        foreach (int tool in new[] { 0, 1 })
        {
            foreach (string phase in new[] { "tfree", "tpre", "tpost" })
            {
                sd.WriteSys($"{phase}{tool}.g",
                    $"set global.toolSeq = global.toolSeq + 1\nset global.{phase}{tool}At = global.toolSeq\n");
            }
        }
    }

    /// <summary>
    /// Poll an object model expression until it reads the expected number, failing with the last
    /// observed text on timeout. Used for values that settle after the code's reply
    /// </summary>
    private static async Task WaitForNumberAsync(DcsTestHost host, string expression, double expected, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string text;
        do
        {
            text = await host.EvaluateRawAsync(expression);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && Math.Abs(value - expected) < 1e-3)
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail($"{what}: {expression} reads '{text}', expected {expected}");
    }

    /// <summary>
    /// Read a numeric object model expression, failing the test with the observed text when it
    /// does not evaluate to a number (a missing field reads as an error message or null)
    /// </summary>
    private static async Task<double> EvaluateNumberAsync(DcsTestHost host, string expression)
    {
        string text = await host.EvaluateRawAsync(expression);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            Assert.Fail($"{expression} did not evaluate to a number: '{text}'");
            return double.NaN;
        }
        return value;
    }

    /// <summary>Poll an object model expression until it reads the expected text</summary>
    private static async Task WaitForTextAsync(DcsTestHost host, string expression, string expected, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string text;
        do
        {
            text = await host.EvaluateRawAsync(expression);
            if (text == expected)
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail($"{what}: {expression} reads '{text}', expected '{expected}'");
    }

    /// <summary>
    /// M563 creates a tool whose number, name, heaters, extruders, fan map, mix, spindle and
    /// state all land in tools[]
    /// </summary>
    /// <remarks>
    /// GCodes::ManageTool and Tool::Create (GCodes.cpp, Tool.cpp): a new tool starts off with
    /// mix 1:0:0, no spindle, zero offsets, and active/standby temperatures of ABS_ZERO
    /// (-273.15 C); the reported fields are Tool's object model table (Tool.cpp)
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task M563CreatesToolWithMappedComponents()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterToolConfig);
        DcsTestHost host = bench.Host;

        double toolCount = await host.EvaluateAsync("#tools");
        double number = await host.EvaluateAsync("tools[0].number");
        string name = await host.EvaluateRawAsync("tools[0].name");
        double heaterCount = await host.EvaluateAsync("#tools[0].heaters");
        double heater0 = await host.EvaluateAsync("tools[0].heaters[0]");
        double extruderCount = await host.EvaluateAsync("#tools[0].extruders");
        double extruder0 = await host.EvaluateAsync("tools[0].extruders[0]");
        double fan0 = await host.EvaluateAsync("tools[0].fans[0]");
        double mix0 = await host.EvaluateAsync("tools[0].mix[0]");
        double spindle = await host.EvaluateAsync("tools[0].spindle");
        double spindleRpm = await host.EvaluateAsync("tools[0].spindleRpm");
        string state = await host.EvaluateRawAsync("tools[0].state");
        double offsetX = await host.EvaluateAsync("tools[0].offsets[0]");
        double offsetY = await host.EvaluateAsync("tools[0].offsets[1]");
        double active0 = await host.EvaluateAsync("tools[0].active[0]");
        double standby0 = await host.EvaluateAsync("tools[0].standby[0]");

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
            Assert.That(state, Is.EqualTo("off"), "a new tool's state is off (RRF Tool::Create ToolState::off)");
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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M563 P0 S\"Renamed\"");

        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("#tools"), Is.EqualTo(1), "the redefined tool still exists (RRF GCodes::ManageTool)");
            Assert.That(await host.EvaluateRawAsync("tools[0].name"), Is.EqualTo("Renamed"), "M563 S renames tools[0].name (RRF GCodes::ManageTool)");
            Assert.That(await host.EvaluateAsync("#tools[0].heaters"), Is.EqualTo(0), "a redefinition without H drops tools[0].heaters (RRF GCodes::ManageTool recreates the tool)");
            Assert.That(await host.EvaluateAsync("#tools[0].extruders"), Is.EqualTo(0), "a redefinition without D drops tools[0].extruders (RRF GCodes::ManageTool recreates the tool)");
        });
    }

    /// <summary>M563 P... D-1 H-1 deletes the tool, deselecting it first if it is current</summary>
    /// <remarks>
    /// GCodes::ManageTool (GCodes.cpp): a movement state whose current tool is the deleted number
    /// runs SelectTool(-1), then Tool::DeleteTool recomputes numToolsToReport, so #tools drops to 0
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task M563DeleteRemovesToolAndDeselects()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(0), "T0 selects the tool before the deletion");

        await host.ExecuteCodeAsync("M563 P0 D-1 H-1");

        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("#tools"), Is.EqualTo(0), "M563 P0 D-1 H-1 removes the tool from tools[] (RRF Tool::DeleteTool)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(-1), "deleting the current tool deselects it (RRF GCodes::ManageTool SelectTool(-1))");
        });
    }

    /// <summary>A bare M563 P reports the tool in RepRapFirmware's format, matching the model</summary>
    /// <remarks>Tool::PrintTool (Tool.cpp): "Tool 0 - name: ...; drives: ...; ...; status: off"</remarks>
    /// TODO fix scenario
    [Test]
    public async Task M563BareReportsToolConsistentWithModel()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterToolConfig);
        DcsTestHost host = bench.Host;

        string reply = (await host.ExecuteCodeAsync("M563 P0")).Trim();
        string name = await host.EvaluateRawAsync("tools[0].name");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "M563 P0 reports the tool number (RRF Tool::PrintTool)");
            Assert.That(reply, Does.Contain($"name: {name}"), "M563 P0 reports the model's tool name (RRF Tool::PrintTool)");
            Assert.That(reply, Does.Contain("drives: 0"), "M563 P0 reports the mapped drive (RRF Tool::PrintTool)");
            Assert.That(reply, Does.Contain("status: off"), "an unselected tool reports status off (RRF Tool::PrintTool)");
        });
    }

    /// <summary>A bare T reports the current tool, consistent with state.currentTool</summary>
    /// <remarks>GCodes::HandleTcode (GCodes2.cpp): "No tool is selected" / "Tool %d is selected"</remarks>
    [Test]
    public async Task BareTReportsCurrentTool()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        string replyNone = (await host.ExecuteCodeAsync("T")).Trim();
        Assert.That(replyNone, Is.EqualTo("No tool is selected"), "bare T with state.currentTool -1 (RRF GCodes::HandleTcode)");

        await host.ExecuteCodeAsync("T0");
        string replySelected = (await host.ExecuteCodeAsync("T")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(0), "T0 sets state.currentTool (RRF MovementState::SelectTool)");
            Assert.That(replySelected, Is.EqualTo("Tool 0 is selected"), "bare T reports the selected tool (RRF GCodes::HandleTcode)");
        });
    }

    /// <summary>
    /// T selects a tool and T-1 deselects it, moving state.currentTool and the tools' own states
    /// between active and standby
    /// </summary>
    /// <remarks>
    /// MovementState::SelectTool (RawMove.cpp) puts the outgoing tool in standby and activates the
    /// new one; Tool::Activate and Tool::Standby (Tool.cpp) set tools[].state
    /// </remarks>
    [Test]
    public async Task ToolSelectionUpdatesCurrentToolAndStates()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: TwoBareToolsConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(0), "T0 sets state.currentTool to 0 (RRF MovementState::SelectTool)");
            Assert.That(await host.EvaluateRawAsync("tools[0].state"), Is.EqualTo("active"), "T0 activates tool 0 (RRF Tool::Activate)");
        });

        await host.ExecuteCodeAsync("T1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(1), "T1 sets state.currentTool to 1 (RRF MovementState::SelectTool)");
            Assert.That(await host.EvaluateRawAsync("tools[0].state"), Is.EqualTo("standby"), "the outgoing tool goes to standby (RRF Tool::Standby)");
            Assert.That(await host.EvaluateRawAsync("tools[1].state"), Is.EqualTo("active"), "the incoming tool becomes active (RRF Tool::Activate)");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(-1), "T-1 deselects the tool (RRF MovementState::SelectTool)");
            Assert.That(await host.EvaluateRawAsync("tools[1].state"), Is.EqualTo("standby"), "a deselected tool goes to standby (RRF Tool::Standby)");
        });
    }

    /// <summary>
    /// A tool change runs tfree for the old tool, then tpre for the new tool, then tpost for the
    /// new tool, in that order; a change with no old tool skips tfree, and T-1 runs only tfree
    /// </summary>
    /// <remarks>
    /// GCodes4.cpp states toolChange0 (tfree of the old tool), toolChange1 (old tool to standby,
    /// then tpre of the new tool) and toolChange2 (SelectTool, then tpost of the new tool)
    /// </remarks>
    [Test]
    public async Task ToolChangeMacrosRunInRrfOrder()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: TwoBareToolsConfig, prepareSd: WriteToolChangeMacros);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(2), "T0 with no old tool runs two macros (RRF toolChange0..2)");
            Assert.That(await host.GlobalAsync("tfree0At"), Is.EqualTo(0), "no tfree runs when no tool was selected (RRF toolChange0)");
            Assert.That(await host.GlobalAsync("tpre0At"), Is.EqualTo(1), "tpre0 runs first (RRF toolChange1)");
            Assert.That(await host.GlobalAsync("tpost0At"), Is.EqualTo(2), "tpost0 runs after tpre0 (RRF toolChange2)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(0), "the change ends with tool 0 selected");
        });

        await host.ExecuteCodeAsync("T1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(5), "T1 from T0 runs three macros (RRF toolChange0..2)");
            Assert.That(await host.GlobalAsync("tfree0At"), Is.EqualTo(3), "tfree of the old tool runs first (RRF toolChange0)");
            Assert.That(await host.GlobalAsync("tpre1At"), Is.EqualTo(4), "tpre of the new tool runs second (RRF toolChange1)");
            Assert.That(await host.GlobalAsync("tpost1At"), Is.EqualTo(5), "tpost of the new tool runs last (RRF toolChange2)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(1), "the change ends with tool 1 selected");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(6), "T-1 runs one macro (RRF toolChange0..2, no new tool)");
            Assert.That(await host.GlobalAsync("tfree1At"), Is.EqualTo(6), "T-1 runs tfree of the old tool (RRF toolChange0)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(-1), "T-1 deselects the tool");
        });
    }

    /// <summary>
    /// T's P parameter is a macro bitmap: bit 0 tfree, bit 1 tpre, bit 2 tpost. P0 suppresses all
    /// three while still selecting the tool, and P1 runs only tfree of the old tool
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode passes P to StartToolChange (GCodes2.cpp); TFreeBit/TPreBit/TPostBit
    /// (Tool.h) gate DoFileMacro in the toolChange0..2 states (GCodes4.cpp)
    /// </remarks>
    [Test]
    public async Task ToolChangeParamBitmapSuppressesMacros()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: TwoBareToolsConfig, prepareSd: WriteToolChangeMacros);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0 P0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(0), "T0 P0 runs no tool change macro (RRF toolChangeParam)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(0), "T0 P0 still selects the tool (RRF MovementState::SelectTool)");
        });

        await host.ExecuteCodeAsync("T1 P1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(1), "T1 P1 runs exactly one macro (RRF TFreeBit)");
            Assert.That(await host.GlobalAsync("tfree0At"), Is.EqualTo(1), "P1 keeps tfree of the old tool (RRF TFreeBit)");
            Assert.That(await host.GlobalAsync("tpre1At"), Is.EqualTo(0), "P1 suppresses tpre (RRF TPreBit)");
            Assert.That(await host.GlobalAsync("tpost1At"), Is.EqualTo(0), "P1 suppresses tpost (RRF TPostBit)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(1), "T1 P1 still selects the tool");
        });
    }

    /// <summary>
    /// T R1 selects the tool recorded in restore point 1: with no pause recorded that tool number
    /// is -1, so T R1 deselects the current tool
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode (GCodes2.cpp) reads restorePoints[R].toolNumber; RestorePoint::Init
    /// (RestorePoint.cpp) starts toolNumber at -1
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task TR1SelectsRestorePointTool()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(0), "T0 selected the tool");

        await host.ExecuteCodeAsync("T R1");
        Assert.Multiple(async () =>
        {
            Assert.That(await EvaluateNumberAsync(host, "state.restorePoints[1].toolNumber"), Is.EqualTo(-1),
                        "no pause has recorded a tool (RRF RestorePoint::Init)");
            Assert.That(await host.EvaluateAsync("state.currentTool"), Is.EqualTo(-1),
                        "T R1 selects restorePoints[1].toolNumber, which is -1 (RRF GCodes::HandleTcode)");
        });
    }

    /// <summary>M567 sets the tool's mix ratios and the bare form reports them</summary>
    /// <remarks>
    /// The M567 case in GCodes2.cpp: E values go to Tool::DefineMix (Tool.cpp), reported in
    /// tools[].mix; the report format is "Tool %d mix ratios: %.3f:%.3f"
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task M567SetsAndReportsMixRatio()
    {
        const string mixConfig = """
            M569 P1.3 S1
            M584 E1.2:1.3
            M563 P0 S"Mixer" D0:1
            """;
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: mixConfig);
        DcsTestHost host = bench.Host;

        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].mix[0]"), Is.EqualTo(1).Within(1e-6), "the initial mix is 1:0 (RRF Tool::Create)");
            Assert.That(await host.EvaluateAsync("tools[0].mix[1]"), Is.EqualTo(0).Within(1e-6), "the initial mix is 1:0 (RRF Tool::Create)");
        });

        await host.ExecuteCodeAsync("M567 P0 E0.6:0.4");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].mix[0]"), Is.EqualTo(0.6).Within(1e-6), "M567 E sets tools[0].mix[0] (RRF Tool::DefineMix)");
            Assert.That(await host.EvaluateAsync("tools[0].mix[1]"), Is.EqualTo(0.4).Within(1e-6), "M567 E sets tools[0].mix[1] (RRF Tool::DefineMix)");
        });

        string reply = (await host.ExecuteCodeAsync("M567 P0")).Trim();
        Assert.That(reply, Does.Contain("Tool 0 mix ratios: 0.600:0.400"), "bare M567 reports the mix (RRF GCodes2.cpp M567 case)");
    }

    /// <summary>
    /// M568 sets the tool's active and standby temperatures, pushes them to the mapped heater's
    /// setpoints, and A moves the heater between off, standby and active
    /// </summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 568 (GCodes.cpp): S/R go through
    /// Tool::SetToolHeaterActiveOrStandbyTemperature, which also sets the heater setpoint when no
    /// other current tool uses the heater; A drives Tool::HeatersToOff/HeatersToActiveOrStandby.
    /// The heater's reported state combines the board-reported mode with the local active flag
    /// (Heater::GetStatus, Heater.cpp), so the test plays the board's status reports
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task M568SetsTemperaturesAndHeaterStates()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M568 P0 S210 R180");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].active[0]"), Is.EqualTo(210), "M568 S sets tools[0].active (RRF SetToolHeaterActiveTemperature)");
            Assert.That(await host.EvaluateAsync("tools[0].standby[0]"), Is.EqualTo(180), "M568 R sets tools[0].standby (RRF SetToolHeaterStandbyTemperature)");
            Assert.That(await host.EvaluateAsync("heat.heaters[0].active"), Is.EqualTo(210), "the tool temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
            Assert.That(await host.EvaluateAsync("heat.heaters[0].standby"), Is.EqualTo(180), "the tool temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
        });

        await host.ExecuteCodeAsync("M568 P0 A2");
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 30.0f);
        await WaitForTextAsync(host, "heat.heaters[0].state", "active",
                               "M568 A2 puts the heater in active (RRF Tool::HeatersToActiveOrStandby, Heater::GetStatus)");

        await host.ExecuteCodeAsync("M568 P0 A1");
        await WaitForTextAsync(host, "heat.heaters[0].state", "standby",
                               "M568 A1 puts the heater in standby (RRF Tool::HeatersToActiveOrStandby, Heater::GetStatus)");

        await host.ExecuteCodeAsync("M568 P0 A0");
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Off, currentTemperature: 30.0f);
        await WaitForTextAsync(host, "heat.heaters[0].state", "off",
                               "M568 A0 switches the heater off (RRF Tool::HeatersToOff, Heater::GetStatus)");

        string reply = (await host.ExecuteCodeAsync("M568 P0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "bare M568 reports the tool (RRF SetOrReportOffsets)");
            Assert.That(reply, Does.Contain("active/standby temperature(s) 210.0/180.0"), "bare M568 reports the model temperatures (RRF SetOrReportOffsets)");
        });
    }

    /// <summary>G10 with axis letters sets the tool's offsets</summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 10 (GCodes.cpp): each seen axis letter goes to
    /// Tool::SetOffset, reported in tools[].offsets
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task G10SetsToolOffsets()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G10 P0 X5 Y-2");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].offsets[0]"), Is.EqualTo(5).Within(1e-6), "G10 X sets tools[0].offsets[0] (RRF Tool::SetOffset)");
            Assert.That(await host.EvaluateAsync("tools[0].offsets[1]"), Is.EqualTo(-2).Within(1e-6), "G10 Y sets tools[0].offsets[1] (RRF Tool::SetOffset)");
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
    /// TODO fix scenario
    [Test]
    public async Task G10SetsToolTemperatures()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G10 P0 S200 R150");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].active[0]"), Is.EqualTo(200), "G10 S sets tools[0].active (RRF SetOrReportOffsets code 10)");
            Assert.That(await host.EvaluateAsync("tools[0].standby[0]"), Is.EqualTo(150), "G10 R sets tools[0].standby (RRF SetOrReportOffsets code 10)");
            Assert.That(await host.EvaluateAsync("heat.heaters[0].active"), Is.EqualTo(200), "the G10 temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
            Assert.That(await host.EvaluateAsync("heat.heaters[0].standby"), Is.EqualTo(150), "the G10 temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
        });
    }

    /// <summary>
    /// M950 R creates a spindle with RepRapFirmware's default RPM range, L changes the range, and
    /// the bare form reports the spindle
    /// </summary>
    /// <remarks>
    /// Spindle::Configure (Spindle.cpp): a seen parameter moves the state from unconfigured to
    /// stopped; min/max default to DefaultMinSpindleRpm 60 and DefaultMaxSpindleRpm 10000
    /// (Configuration.h); the reported fields are Spindle's object model table
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task M950CreatesSpindleWithDefaultsAndLimits()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M950 R0 C\"1.out6\"");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("stopped"), "M950 R moves the spindle to stopped (RRF Spindle::Configure)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(0), "a new spindle's configured RPM is 0 (RRF Spindle constructor)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(0), "a new spindle's current RPM is 0 (RRF Spindle constructor)");
            Assert.That(await host.EvaluateAsync("spindles[0].min"), Is.EqualTo(60), "spindles[0].min defaults to DefaultMinSpindleRpm (RRF Configuration.h)");
            Assert.That(await host.EvaluateAsync("spindles[0].max"), Is.EqualTo(10000), "spindles[0].max defaults to DefaultMaxSpindleRpm (RRF Configuration.h)");
        });

        await host.ExecuteCodeAsync("M950 R0 L4000:12000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("spindles[0].min"), Is.EqualTo(4000), "M950 L with two values sets spindles[0].min (RRF Spindle::Configure)");
            Assert.That(await host.EvaluateAsync("spindles[0].max"), Is.EqualTo(12000), "M950 L with two values sets spindles[0].max (RRF Spindle::Configure)");
        });

        string reply = (await host.ExecuteCodeAsync("M950 R0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Spindle 0"), "bare M950 R reports the spindle (RRF Spindle::Configure)");
            Assert.That(reply, Does.Contain("rpm min 4000, max 12000"), "the reported range matches the model (RRF Spindle::Configure)");
        });
    }

    /// <summary>
    /// In CNC mode M3 runs the spindle forward, M4 reverse, and M5 stops it; M3 without P and
    /// without a spindle tool is refused
    /// </summary>
    /// <remarks>
    /// The M3/M4/M5 cases in GCodes2.cpp: S sets the configured RPM (spindles[].active), the state
    /// change propagates the RPM to spindles[].current (Spindle::SetState and Spindle::SetRpm),
    /// and M5 zeroes current while active keeps the configured value
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task SpindleControlCodesDriveSpindleState()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: SpindleCncConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M3 P0 S5000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("forward"), "M3 sets spindles[0].state to forward (RRF GCodes2.cpp M3 case)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(5000), "M3 S sets spindles[0].active (RRF Spindle::SetConfiguredRpm)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(5000), "the state change applies the RPM to spindles[0].current (RRF Spindle::SetRpm)");
        });

        await host.ExecuteCodeAsync("M4 P0 S6000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("reverse"), "M4 sets spindles[0].state to reverse (RRF GCodes2.cpp M4 case)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(6000), "M4 S sets spindles[0].active (RRF Spindle::SetConfiguredRpm)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(6000), "the state change applies the RPM to spindles[0].current (RRF Spindle::SetRpm)");
        });

        await host.ExecuteCodeAsync("M5");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("stopped"), "M5 without P stops every configured spindle (RRF GCodes2.cpp M5 case)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(0), "a stopped spindle's current RPM is 0 (RRF Spindle::SetRpm)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(6000), "M5 keeps the configured RPM in spindles[0].active (RRF Spindle::SetState)");
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
    /// TODO fix scenario
    [Test]
    public async Task SpindleToolSelectionAndRpm()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: SpindleCncConfig + "\nM563 P0 S\"Cutter\" R0\n");
        DcsTestHost host = bench.Host;

        Assert.That(await host.EvaluateAsync("tools[0].spindle"), Is.EqualTo(0), "M563 R0 sets tools[0].spindle (RRF GCodes::ManageTool)");

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("stopped"), "selecting a spindle tool stops the spindle (RRF Tool::Activate)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(0), "selecting restores the tool's configured RPM, still 0 (RRF Tool::Activate)");
        });

        await host.ExecuteCodeAsync("M3 S5000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].spindleRpm"), Is.EqualTo(5000), "M3 S on the current tool sets tools[0].spindleRpm (RRF Tool::SetSpindleRpm)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(5000), "the tool RPM reaches spindles[0].active (RRF Spindle::SetConfiguredRpm)");
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("forward"), "M3 runs the spindle forward (RRF GCodes2.cpp M3 case)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(5000), "the running spindle turns at the set RPM (RRF Spindle::SetRpm)");
        });

        await host.ExecuteCodeAsync("M568 P0 F3000");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateAsync("tools[0].spindleRpm"), Is.EqualTo(3000), "M568 F sets tools[0].spindleRpm (RRF SetOrReportOffsets code 568)");
            Assert.That(await host.EvaluateAsync("spindles[0].active"), Is.EqualTo(3000), "M568 F on the current tool updates spindles[0].active (RRF Tool::SetSpindleRpm)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(3000), "the running spindle follows the new RPM (RRF Spindle::SetConfiguredRpm with updateCurrentRpm)");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("spindles[0].state"), Is.EqualTo("stopped"), "deselecting a spindle tool stops the spindle (RRF Tool::Standby)");
            Assert.That(await host.EvaluateAsync("spindles[0].current"), Is.EqualTo(0), "a stopped spindle's current RPM is 0 (RRF Spindle::SetRpm)");
            Assert.That(await host.EvaluateAsync("tools[0].spindleRpm"), Is.EqualTo(3000), "the tool keeps its RPM for the next selection (RRF Tool::Standby)");
        });
    }

    /// <summary>
    /// M451 and M453 switch state.machineMode between FFF and CNC, and M450 reports the mode in
    /// RepRapFirmware's format
    /// </summary>
    /// <remarks>
    /// The M450, M451 and M453 cases in GCodes2.cpp set machineType, reported through
    /// GCodes::GetMachineModeString (GCodes.cpp) as "FFF"/"CNC"/"Laser"; M450's reply is
    /// "PrinterMode:%s"
    /// </remarks>
    [Test]
    public async Task MachineModeCodesSwitchAndReport()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        DcsTestHost host = bench.Host;

        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("state.machineMode"), Is.EqualTo("FFF"), "the machine starts in FFF mode (RRF MachineType::fff default)");
            Assert.That((await host.ExecuteCodeAsync("M450")).Trim(), Is.EqualTo("PrinterMode:FFF"), "M450 reports the mode (RRF GCodes2.cpp M450 case)");
        });

        await host.ExecuteCodeAsync("M453");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("state.machineMode"), Is.EqualTo("CNC"), "M453 sets state.machineMode to CNC (RRF GCodes2.cpp M453 case)");
            Assert.That((await host.ExecuteCodeAsync("M450")).Trim(), Is.EqualTo("PrinterMode:CNC"), "M450 reports the new mode (RRF GCodes2.cpp M450 case)");
        });

        await host.ExecuteCodeAsync("M451");
        Assert.That(await host.EvaluateRawAsync("state.machineMode"), Is.EqualTo("FFF"),
                    "M451 sets state.machineMode back to FFF (RRF GCodes2.cpp M451 case)");
    }

    /// <summary>
    /// In laser mode M3 S sets the beam power for subsequent moves rather than driving a spindle:
    /// state.laserPwm changes only when a move is built, and M5 turns the beam off
    /// </summary>
    /// <remarks>
    /// The M452 case in GCodes2.cpp (S1 makes the power sticky across moves); the laser branch of
    /// the M3/M5 cases stores the power in the movement state's laser pixel data, and
    /// DoStraightMove copies it into the move (GCodes.cpp), where state.laserPwm reads it scaled
    /// to 0..1 (GCodes::GetLaserPwm, GCodes.h, and RepRap.cpp state table)
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task LaserModeAppliesM3PowerToMoves()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M452 S1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.EvaluateRawAsync("state.machineMode"), Is.EqualTo("Laser"), "M452 sets state.machineMode to Laser (RRF GCodes2.cpp M452 case)");
            Assert.That(await EvaluateNumberAsync(host, "state.laserPwm"), Is.EqualTo(0).Within(1e-3), "laser mode starts with the beam off (RRF GCodes::GetLaserPwm)");
        });

        await host.ExecuteCodeAsync("M3 S255");
        Assert.That(await EvaluateNumberAsync(host, "state.laserPwm"), Is.EqualTo(0).Within(1e-3),
                    "M3 S alone does not fire the beam; the power applies to moves (RRF M3 laser branch, DoStraightMove)");

        await host.ExecuteCodeAsync("G91");
        await host.ExecuteCodeAsync("G1 X1 F6000");
        await WaitForNumberAsync(host, "state.laserPwm", 1.0,
                                 "the move carries M3's sticky power, 255 of 255 (RRF DoStraightMove, GCodes::GetLaserPwm)");

        await host.ExecuteCodeAsync("M5");
        await host.ExecuteCodeAsync("G1 X1 F6000");
        await WaitForNumberAsync(host, "state.laserPwm", 0.0,
                                 "M5 clears the laser power for later moves (RRF M5 laser branch)");
    }
}
