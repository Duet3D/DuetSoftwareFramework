using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>T, selecting a tool, its change macros and the restore point tool</summary>
[TestFixture]
[GCode("T")]
public class TTests : BenchFixture
{
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

    /// <summary>A bare T reports the current tool, consistent with state.currentTool</summary>
    /// <remarks>GCodes::HandleTcode (GCodes2.cpp): "No tool is selected" / "Tool %d is selected"</remarks>
    [Test]
    public async Task BareTReportsCurrentTool()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        string replyNone = (await host.ExecuteCodeAsync("T")).Trim();
        Assert.That(replyNone, Is.EqualTo("No tool is selected"), "bare T with state.currentTool -1 (RRF GCodes::HandleTcode)");

        await host.ExecuteCodeAsync("T0");
        string replySelected = (await host.ExecuteCodeAsync("T")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 sets state.currentTool (RRF MovementState::SelectTool)");
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
        await using Bench bench = await Bench.StartAsync(TwoBareToolsConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 sets state.currentTool to 0 (RRF MovementState::SelectTool)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Active), "T0 activates tool 0 (RRF Tool::Activate)");
        });

        await host.ExecuteCodeAsync("T1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(1), "T1 sets state.currentTool to 1 (RRF MovementState::SelectTool)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Standby), "the outgoing tool goes to standby (RRF Tool::Standby)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[1]!.State), Is.EqualTo(ToolState.Active), "the incoming tool becomes active (RRF Tool::Activate)");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "T-1 deselects the tool (RRF MovementState::SelectTool)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[1]!.State), Is.EqualTo(ToolState.Standby), "a deselected tool goes to standby (RRF Tool::Standby)");
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
        await using Bench bench = await Bench.With(TwoBareToolsConfig).WithSd(WriteToolChangeMacros).StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(2), "T0 with no old tool runs two macros (RRF toolChange0..2)");
            Assert.That(await host.GlobalAsync("tfree0At"), Is.EqualTo(0), "no tfree runs when no tool was selected (RRF toolChange0)");
            Assert.That(await host.GlobalAsync("tpre0At"), Is.EqualTo(1), "tpre0 runs first (RRF toolChange1)");
            Assert.That(await host.GlobalAsync("tpost0At"), Is.EqualTo(2), "tpost0 runs after tpre0 (RRF toolChange2)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "the change ends with tool 0 selected");
        });

        await host.ExecuteCodeAsync("T1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(5), "T1 from T0 runs three macros (RRF toolChange0..2)");
            Assert.That(await host.GlobalAsync("tfree0At"), Is.EqualTo(3), "tfree of the old tool runs first (RRF toolChange0)");
            Assert.That(await host.GlobalAsync("tpre1At"), Is.EqualTo(4), "tpre of the new tool runs second (RRF toolChange1)");
            Assert.That(await host.GlobalAsync("tpost1At"), Is.EqualTo(5), "tpost of the new tool runs last (RRF toolChange2)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(1), "the change ends with tool 1 selected");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(6), "T-1 runs one macro (RRF toolChange0..2, no new tool)");
            Assert.That(await host.GlobalAsync("tfree1At"), Is.EqualTo(6), "T-1 runs tfree of the old tool (RRF toolChange0)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "T-1 deselects the tool");
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
        await using Bench bench = await Bench.With(TwoBareToolsConfig).WithSd(WriteToolChangeMacros).StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0 P0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(0), "T0 P0 runs no tool change macro (RRF toolChangeParam)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 P0 still selects the tool (RRF MovementState::SelectTool)");
        });

        await host.ExecuteCodeAsync("T1 P1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalAsync("toolSeq"), Is.EqualTo(1), "T1 P1 runs exactly one macro (RRF TFreeBit)");
            Assert.That(await host.GlobalAsync("tfree0At"), Is.EqualTo(1), "P1 keeps tfree of the old tool (RRF TFreeBit)");
            Assert.That(await host.GlobalAsync("tpre1At"), Is.EqualTo(0), "P1 suppresses tpre (RRF TPreBit)");
            Assert.That(await host.GlobalAsync("tpost1At"), Is.EqualTo(0), "P1 suppresses tpost (RRF TPostBit)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(1), "T1 P1 still selects the tool");
        });
    }

    /// <summary>Tool 0's active and standby temperatures in <see cref="HeatedToolsConfig"/></summary>
    private const int Tool0Active = 200, Tool0Standby = 150;

    /// <summary>Tool 1's active and standby temperatures in <see cref="HeatedToolsConfig"/></summary>
    private const int Tool1Active = 210, Tool1Standby = 160;

    /// <summary>
    /// Heaters 0 and 1 on <see cref="Machines.DriverBoard"/>, tool 0 using heater 0 and tool 1 heater 1, each with
    /// its temperatures set, and a global the tool change macros of <see cref="WriteLoggingMacros"/> append to
    /// </summary>
    private static readonly string HeatedToolsConfig = $"""
        M308 S0 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor" T100000 B4138
        M950 H0 C"{Expansion.Out(Machines.DriverBoard, 0)}" T0
        M308 S1 P"{Expansion.Temp(Machines.DriverBoard, 1)}" Y"thermistor" T100000 B4138
        M950 H1 C"{Expansion.Out(Machines.DriverBoard, 1)}" T1
        M563 P0 S"Zero" H0
        M563 P1 S"One" H1
        M568 P0 S{Tool0Active} R{Tool0Standby}
        M568 P1 S{Tool1Active} R{Tool1Standby}
        global log = ""
        """;

    /// <summary>Write a macro that appends its own name and a semicolon to global.log</summary>
    private static void WriteLoggingMacro(VirtualSd sd, string name)
        => sd.WriteSys($"{name}.g", $"set global.log = global.log ^ \"{name};\"\n");

    /// <summary>Write tfree, tpre and tpost for each of the given tools, each logging itself to global.log</summary>
    private static void WriteLoggingMacros(VirtualSd sd, params int[] tools)
    {
        foreach (int tool in tools)
        {
            foreach (string phase in new[] { "tfree", "tpre", "tpost" })
            {
                WriteLoggingMacro(sd, $"{phase}{tool}");
            }
        }
    }

    /// <summary>Every heater setpoint message sent, with the board it went to</summary>
    private static (byte Board, byte Heater, byte Command, byte Function, float SetPoint)[] TemperatureMessages(Bench bench)
        => [.. bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>()
                .Select(m => (m.Board, m.Message.HeaterNumber, m.Message.Command, m.Message.Function, m.Message.SetPoint))];

    /// <summary>One message switching a tool heater on <see cref="Machines.DriverBoard"/> on at a setpoint</summary>
    private static (byte Board, byte Heater, byte Command, byte Function, float SetPoint) HeaterOn(int heater, float setPoint)
        => (Machines.DriverBoard, (byte)heater, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)HeaterFunction.Tool, setPoint);

    /// <summary>Start a bench with <see cref="HeatedToolsConfig"/>, both heaters reporting a room temperature</summary>
    private static async Task<Bench> StartHeatedToolsAsync(Action<VirtualSd>? files = null)
    {
        Bench bench = await Bench.With(HeatedToolsConfig).WithSd(files).StartAsync();
        await bench.ReportHeaterAsync(0, HeaterMode.Off, 25.0f);
        await bench.ReportHeaterAsync(1, HeaterMode.Off, 25.0f);
        return bench;
    }

    /// <summary>
    /// A tool change records the tool it selects as the next tool and the one it replaces as the previous tool, in
    /// state and in move.motionSystems[0], and the selected tool in both places
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode sets ms.newToolNumber; toolChange0 (GCodes4.cpp) calls MovementState::SetPreviousToolNumber;
    /// RawMove.cpp publishes currentTool, nextTool and previousTool per motion system. Regression: t-deselect-tool,
    /// t-report-current-tool, m109-set-and-wait-with-r-and-tool
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ToolChangeRecordsCurrentNextAndPreviousTool()
    {
        await using Bench bench = await Bench.StartAsync(TwoBareToolsConfig);
        DcsTestHost host = bench.Host;

#pragma warning disable CS0618
        async Task ExpectAsync(string code, int current, int next, int previous)
        {
            Assert.That(await host.ExecuteCodeAsync(code), Reply.IsEmpty, $"{code} replies nothing");
            (int, int, int, int, int, int) seen = await host.ReadModelAsync(model => (
                model.State.CurrentTool, model.State.NextTool, model.State.PreviousTool,
                model.Move.MotionSystems[0].CurrentTool, model.Move.MotionSystems[0].NextTool, model.Move.MotionSystems[0].PreviousTool));
            Assert.That(seen, Is.EqualTo((current, next, previous, current, next, previous)),
                        $"after {code}: state.currentTool/nextTool/previousTool, then the same in move.motionSystems[0]");
        }
#pragma warning restore CS0618

        await ExpectAsync("T0", current: 0, next: 0, previous: -1);
        await ExpectAsync("T1", current: 1, next: 1, previous: 0);
        await ExpectAsync("T-1", current: -1, next: -1, previous: 1);
        await ExpectAsync("T0 P0", current: 0, next: 0, previous: -1);
    }

    /// <summary>
    /// A tool change saves restore point 2 before tfree runs: the user position, the feed rate of the code's input,
    /// the tool being replaced and the virtual fan speed, also with P0. It changes no fan, and the input's feed rate
    /// is what it was before, whatever the macros set
    /// </summary>
    /// <remarks>
    /// toolChange0 (GCodes4.cpp) calls SavePosition(ToolChangeRestorePointNumber) and records toolNumber and
    /// virtualFanSpeed whatever toolChangeParam says; toolChangeComplete restores the feed rate from the restore point
    /// and deliberately leaves the fans alone. Regression: t-select-without-macros, t-deselect-tool
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ToolChangeSavesRestorePoint2()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, TwoBareToolsConfig)
                                             .WithSd(sd => sd.WriteSys("tpost0.g", "G1 F9000\n")).StartAsync();
        DcsTestHost host = bench.Host;

#pragma warning disable CS0618
        Task<(float X, float Y, float Z, float FeedRate, int Tool, float Fan, int LegacyTool)> ReadRestorePoint2Async()
            => host.ReadModelAsync(model =>
            {
                DuetAPI.ObjectModel.RestorePoint rp = model.Move.MotionSystems[0].RestorePoints[2];
                return (rp.Coords[0], rp.Coords[1], rp.Coords[2], rp.FeedRate, rp.ToolNumber, rp.FanPwm, model.State.RestorePoints[2].ToolNumber);
            });
#pragma warning restore CS0618

        await host.ExecuteCodeAsync("G1 X10 Y20 Z5 F1200");
        await host.ExecuteCodeAsync("M106 S0.5");
        float feedRateBefore = await host.ReadInputAsync(input => input.FeedRate);

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadRestorePoint2Async(), Is.EqualTo((10.0f, 20.0f, 5.0f, 1200.0f, -1, 0.5f, -1)),
                        "T0 saved X Y Z, the feed rate as F gave it, no tool and the virtual fan speed (RRF toolChange0)");
            Assert.That(await host.ReadInputAsync(input => input.FeedRate), Is.EqualTo(feedRateBefore),
                        "the feed rate tpost0.g set does not outlast the tool change (RRF toolChangeComplete)");
        });

        await host.ExecuteCodeAsync("G1 X30 F600");
        await host.ExecuteCodeAsync("M106 S0.25");
        await host.ExecuteCodeAsync("T1 P0");
        Assert.That(await ReadRestorePoint2Async(), Is.EqualTo((30.0f, 20.0f, 5.0f, 600.0f, 0, 0.25f, 0)),
                    "T1 P0 still saves restore point 2, now with tool 0 (RRF toolChange0 ignores toolChangeParam)");
    }

    /// <summary>
    /// T's T parameter names the tool as the number does, so that it can be an expression, and T{expression} is the
    /// same thing written without the space
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode handles "T T{expression}" when there is no command number. Regression:
    /// t-select-with-t-parameter
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task TParameterSelectsTool()
    {
        await using Bench bench = await Bench.With(TwoBareToolsConfig).WithSd(WriteToolChangeMacros).StartAsync();
        DcsTestHost host = bench.Host;

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ExecuteCodeAsync("T T1"), Reply.IsEmpty, "T T1 replies nothing");
            Assert.That((await host.ExecuteCodeAsync("T")).Trim(), Is.EqualTo("Tool 1 is selected"), "T T1 selected tool 1");
            Assert.That(await host.GlobalAsync("tpost1At"), Is.EqualTo(2), "T T1 is a full tool change, macros included");

            Assert.That(await host.ExecuteCodeAsync("T T0"), Reply.IsEmpty, "T T0 replies nothing");
            Assert.That((await host.ExecuteCodeAsync("T")).Trim(), Is.EqualTo("Tool 0 is selected"), "T T0 selected tool 0");

            await host.ExecuteCodeAsync("T{state.currentTool + 1}");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(1), "T{expression} selects the tool it evaluates to");

            await host.ExecuteCodeAsync("T T{state.currentTool - 2}");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "T T-1 deselects");
        });
    }

    /// <summary>
    /// T R1 selects the tool recorded in restore point 1, which G60 S1 writes as a pause does, putting the current
    /// tool on standby as any tool change does
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode reads ms.restorePoints[R].toolNumber. Regression: t-reselect-tool-after-pause
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task TR1SelectsTheToolOfThePauseRestorePoint()
    {
        await using Bench bench = await Bench.StartAsync(TwoBareToolsConfig + "\nT1\nG60 S1\nT0");
        DcsTestHost host = bench.Host;

        Assert.That(await host.ExecuteCodeAsync("T R1"), Reply.IsEmpty, "T R1 replies nothing");
        Assert.Multiple(async () =>
        {
            Assert.That((await host.ExecuteCodeAsync("T")).Trim(), Is.EqualTo("Tool 1 is selected"), "T R1 selected the tool G60 S1 recorded");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Standby), "tool 0 went to standby");
            Assert.That(await host.ReadModelAsync(model => model.Tools[1]!.State), Is.EqualTo(ToolState.Active), "tool 1 is active");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].PreviousTool), Is.EqualTo(0), "previousTool");
        });
    }

    /// <summary>
    /// T R1 with no pause recorded selects tool -1, which deselects the current tool
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode (GCodes2.cpp) reads restorePoints[R].toolNumber; RestorePoint::Init
    /// (RestorePoint.cpp) starts toolNumber at -1
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task TR1WithNoPauseDeselects()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 selected the tool");

        await host.ExecuteCodeAsync("T R1");
        Assert.Multiple(async () =>
        {
#pragma warning disable CS0618
            Assert.That(await host.ReadNumberAsync(model => model.State.RestorePoints[1].ToolNumber), Is.EqualTo(-1),
                        "no pause has recorded a tool (RRF RestorePoint::Init)");
#pragma warning restore
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1),
                        "T R1 selects restorePoints[1].toolNumber, which is -1 (RRF GCodes::HandleTcode)");
        });
    }

    /// <summary>T R past the visible restore points is refused and changes nothing</summary>
    /// <remarks>GCodes::HandleTcode reads R with GetLimitedUIValue('R', NumVisibleRestorePoints)</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task TRPastTheRestorePointsIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(TwoBareToolsConfig + "\nT0");
        DcsTestHost host = bench.Host;

        Assert.Multiple(async () =>
        {
            Assert.That(await host.ExecuteCodeAsync($"T R{DuetControlServer.Motion.RestorePoint.NumVisible}"), Reply.IsError(),
                        "R names one of the visible restore points (RRF GetLimitedUIValue)");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "the selection is unchanged");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].PreviousTool), Is.EqualTo(-1), "no tool change started");
        });
    }

    /// <summary>
    /// Selecting a tool that does not exist is not an error: it runs tfree of the current tool, puts it on standby
    /// and leaves no tool selected, with no tpre or tpost for the missing tool. nextTool keeps the number asked for
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode does not check the number; toolChange1 only runs tpre for a tool that exists, toolChange2's
    /// MovementState::SelectTool finds none and tpost needs a current tool. Regression: t-select-undefined-tool
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task UndefinedToolDeselectsTheCurrentTool()
    {
        await using Bench bench = await StartHeatedToolsAsync(sd => WriteLoggingMacros(sd, 0, 5));
        DcsTestHost host = bench.Host;
        await host.ExecuteCodeAsync("T0");
        await host.ExecuteCodeAsync("set global.log = \"\"");
        bench.CanMaster.ClearCapture();

        Assert.That(await host.ExecuteCodeAsync("T5"), Reply.IsEmpty, "T5 replies nothing, as RRF answers a bare ok");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tfree0;"), "only tfree0 ran, not tpre5 or tpost5");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "no tool is selected");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Standby), "tool 0 is on standby");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].NextTool), Is.EqualTo(5), "nextTool is the number asked for");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].PreviousTool), Is.EqualTo(0), "previousTool");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].RestorePoints[2].ToolNumber), Is.EqualTo(0),
                        "restore point 2 recorded tool 0");
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { HeaterOn(0, Tool0Standby) }), "tool 0's heater went to its standby temperature");
            Assert.That((await host.ExecuteCodeAsync("T")).Trim(), Is.EqualTo("No tool is selected"), "bare T agrees");
        });
    }

    /// <summary>
    /// Selecting the tool that is already selected runs no macro and starts no tool change, but switches the tool's
    /// heaters back to their active temperatures, as after a heater fault was cleared
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode calls MovementState::SelectTool for the same tool, which calls Tool::Activate. The wiki's
    /// "the command does nothing" is not what the source does
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ReselectingTheCurrentToolReactivatesIt()
    {
        await using Bench bench = await StartHeatedToolsAsync(sd => WriteLoggingMacros(sd, 0));
        DcsTestHost host = bench.Host;
        await host.ExecuteCodeAsync("T0");
        await host.ExecuteCodeAsync("M568 P0 A0");
        await host.ExecuteCodeAsync("set global.log = \"\"");
        bench.CanMaster.ClearCapture();

        Assert.That(await host.ExecuteCodeAsync("T0"), Reply.IsEmpty, "T0 again replies nothing");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalTextAsync("log"), Is.Empty, "no tool change macro ran");
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { HeaterOn(0, Tool0Active) }), "tool 0's heater is switched on again (RRF Tool::Activate)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Active), "tool 0 is active");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].PreviousTool), Is.EqualTo(-1), "previousTool is left alone");
        });
    }

    /// <summary>
    /// The heaters of a tool change: the outgoing tool's heater goes to its standby temperature before the incoming
    /// tool's goes to its active one, one message each, field by field
    /// </summary>
    /// <remarks>
    /// toolChange1 calls Tool::Standby, toolChange2's SelectTool calls Tool::Activate; Tool::HeatersToActiveOrStandby
    /// sends CanMessageSetHeaterTemperature commandOn through RemoteHeater::SwitchOn. Regression: t-deselect-tool,
    /// t-report-current-tool
    /// </remarks>
    [Test]
    public async Task ToolChangeSwitchesHeatersBetweenStandbyAndActive()
    {
        await using Bench bench = await StartHeatedToolsAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { HeaterOn(0, Tool0Active) }), "T0 switches heater 0 to its active temperature");

        bench.CanMaster.ClearCapture();
        await host.ExecuteCodeAsync("T1");
        Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { HeaterOn(0, Tool0Standby), HeaterOn(1, Tool1Active) }),
                    "T1 puts heater 0 on standby, then switches heater 1 to its active temperature");

        bench.CanMaster.ClearCapture();
        await host.ExecuteCodeAsync("T-1");
        Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { HeaterOn(1, Tool1Standby) }), "T-1 puts heater 1 on standby");
    }

    /// <summary>
    /// A board refusing a heater during a tool change does not fail the T code: the refusal is reported as a message
    /// of its own and the tool is selected all the same
    /// </summary>
    /// <remarks>Tool::HeatersToActiveOrStandby reports a failed heater with Platform::MessageF and carries on</remarks>
    [Test]
    public async Task HeaterRefusalDoesNotStopTheToolChange()
    {
        const string refusal = "heater 0 output is shorted";
        await using Bench bench = await StartHeatedToolsAsync();
        DcsTestHost host = bench.Host;
        bench.CanMaster.ReportWith<CanMessageSetHeaterTemperatureV1>(refusal, CodeResult.Error);

        string reply = await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsNotError, "T0 itself succeeds");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "tool 0 is selected");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Active), "tool 0 is active");
            Assert.That(await host.WaitForMessageAsync(MessageType.Error, refusal), Is.Not.Empty, "the board's refusal is reported");
        });
    }

    /// <summary>
    /// A code in a tool change macro that fails reports its error, and the macro and the tool change carry on
    /// </summary>
    /// <remarks>An error result does not abort a macro in RepRapFirmware; only an exception does (GCodes::AbortPrint)</remarks>
    [Test]
    public async Task ErrorInAToolChangeMacroDoesNotStopTheChange()
    {
        await using Bench bench = await StartHeatedToolsAsync(sd =>
        {
            WriteLoggingMacros(sd, 0);
            sd.WriteSys("tpre0.g", "G10 P99 X1\nset global.log = global.log ^ \"tpre0;\"\n");
        });
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tpre0;tpost0;"), "tpre0 ran on past its error, then tpost0 ran");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "tool 0 is selected");
            Assert.That(await host.WaitForMessageAsync(MessageType.Error, "99"), Is.Not.Empty, "the failing code's error is reported");
        });
    }

    /// <summary>
    /// When tool-specific tool change macros are missing, the generic tfree.g, tpre.g and tpost.g run instead; a
    /// tool-specific one replaces its generic one
    /// </summary>
    /// <remarks>toolChange0..2 (GCodes4.cpp) fall back to TFREE ".g", TPRE ".g" and TPOST ".g" when DoFileMacro finds no tool-specific file</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task GenericToolChangeMacrosStandInForMissingOnes()
    {
        await using Bench bench = await StartHeatedToolsAsync(sd =>
        {
            WriteLoggingMacro(sd, "tfree");
            WriteLoggingMacro(sd, "tpre");
            WriteLoggingMacro(sd, "tpost");
            WriteLoggingMacro(sd, "tpre1");
        });
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tpre;tpost;"), "T0 runs the generic tpre and tpost");

        await host.ExecuteCodeAsync("set global.log = \"\"");
        await host.ExecuteCodeAsync("T1");
        Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tfree;tpre1;tpost;"), "T1 runs the generic tfree, its own tpre1 and the generic tpost");

        await host.ExecuteCodeAsync("set global.log = \"\"");
        await host.ExecuteCodeAsync("T-1");
        Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tfree;"), "T-1 runs the generic tfree only");
    }

    /// <summary>
    /// A tool's offsets apply from the moment it is selected, which is before tpost and not during tpre, until it is
    /// deselected, which is after tfree. The machine does not move: the user position is what changes
    /// </summary>
    /// <remarks>
    /// toolChange1 sets ms.currentTool to null after Tool::Standby and calls UpdateCurrentUserPosition before tpre;
    /// toolChange2 calls it again after MovementState::SelectTool, before tpost. The wiki's T notes say the offsets
    /// apply in tfree and tpost but not in tpre
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ToolOffsetsApplyFromSelectionToDeselection()
    {
        const float x = 50, y = 50, z = 10, offsetX = 5, offsetY = -3, offsetZ = 2;
        await using Bench bench = await Bench.With(Machines.Xyz, $"M563 P0 S\"Offset\"\nG10 P0 X{offsetX} Y{offsetY} Z{offsetZ}\nglobal freeX = -1\nglobal preX = -1\nglobal postX = -1")
                                             .WithSd(sd =>
                                             {
                                                 sd.WriteSys("tfree0.g", "set global.freeX = move.axes[0].userPosition\n");
                                                 sd.WriteSys("tpre0.g", "set global.preX = move.axes[0].userPosition\n");
                                                 sd.WriteSys("tpost0.g", "set global.postX = move.axes[0].userPosition\n");
                                             }).StartAsync();
        DcsTestHost host = bench.Host;
        await host.ExecuteCodeAsync($"G1 X{x} Y{y} Z{z} F6000");
        await bench.WaitForMachinePositionAsync(0, x);

        Task<(float X, float Y, float Z)> UserPositionAsync()
            => host.ReadModelAsync(model => (model.Move.Axes[0].UserPosition ?? float.NaN, model.Move.Axes[1].UserPosition ?? float.NaN, model.Move.Axes[2].UserPosition ?? float.NaN));

        await host.ExecuteCodeAsync("T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalNumberAsync("preX"), Is.EqualTo(x), "no offset applies in tpre0");
            Assert.That(await host.GlobalNumberAsync("postX"), Is.EqualTo(x + offsetX), "tool 0's offset applies in tpost0");
            Assert.That(await UserPositionAsync(), Is.EqualTo((x + offsetX, y + offsetY, z + offsetZ)), "the user position takes tool 0's offsets");
            Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition), Is.EqualTo(x), "the machine has not moved");
        });

        await host.ExecuteCodeAsync("T-1");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalNumberAsync("freeX"), Is.EqualTo(x + offsetX), "tool 0's offset still applies in tfree0");
            Assert.That(await UserPositionAsync(), Is.EqualTo((x, y, z)), "with no tool the user position is the machine position again");
        });
    }

    /// <summary>
    /// The selected tool's X map decides which axis the user's X is: selecting a tool that maps X onto U makes the
    /// user X wherever U is and moves U for a G1 X, and deselecting it gives X back
    /// </summary>
    /// <remarks>UpdateCurrentUserPosition after MovementState::SelectTool runs ToolOffsetInverseTransform with the new tool's map</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task SelectedToolsAxisMapDecidesWhatXIs()
    {
        const int uAxis = 3;
        const float xBefore = 10, uTarget = 20;
        string uDriver = Expansion.Driver(Machines.DriverBoard, Machines.EDriver);
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, $"""
            M569 P{uDriver} S1
            M584 U{uDriver}
            M92 U{Machines.XyStepsPerMm}
            M906 U{Machines.MotorCurrent}
            M208 U0:{Machines.XyMax}
            M563 P0 S"OnU" X{uAxis}
            """);
        DcsTestHost host = bench.Host;
        await host.ExecuteCodeAsync($"G1 X{xBefore} F6000");
        await bench.WaitForMachinePositionAsync(0, xBefore);

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(0.0f), "with tool 0 the user X is where U is");

        await host.ExecuteCodeAsync($"G1 X{uTarget} F6000");
        await bench.WaitForMachinePositionAsync(uAxis, uTarget);
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition), Is.EqualTo(xBefore), "G1 X moved U and not X");

        await host.ExecuteCodeAsync("T-1");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(xBefore), "with no tool the user X is where X is");
    }

    /// <summary>
    /// A job's tool survives a pause that deselects it: the pause records the tool in restore point 1, and resume.g's
    /// T R1 selects it again before the job carries on
    /// </summary>
    /// <remarks>MovementState::SavePosition records the current tool; T R1 is the line the wiki gives resume.g</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ResumeReselectsTheToolThePauseRecorded()
    {
        await using Bench bench = await Bench.With(Machines.Xye, TwoBareToolsConfig, "global toolAfterResume = -2", Machines.Xye.Homed)
                                             .WithSd(sd =>
                                             {
                                                 sd.WriteSys("pause.g", "T-1\n");
                                                 sd.WriteSys("resume.g", "T R1\n");
                                                 sd.WriteGCode("job.gcode", "T1\nG1 X10 F6000\nM226\nset global.toolAfterResume = state.currentTool\n");
                                             }).StartAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "pause.g deselected the tool");
            Assert.That(await host.ReadModelAsync(model => model.Move.MotionSystems[0].RestorePoints[1].ToolNumber), Is.EqualTo(1),
                        "the pause recorded tool 1");
        });

        await host.ExecuteCodeAsync("M24");
        await host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await host.GlobalAsync("toolAfterResume"), Is.EqualTo(1), "resume.g selected tool 1 again before the job carried on");
    }

    /// <summary>
    /// A tool change in a simulated job selects the tool but runs no tool change macro and leaves the heaters and
    /// the tools' states alone
    /// </summary>
    /// <remarks>
    /// GCodes::StartToolChange sets toolChangeParam to 0 while simulating, and MovementState::SelectTool and
    /// toolChange1 skip Tool::Standby and Tool::Activate
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task SimulatedToolChangeOnlySelects()
    {
        await using Bench bench = await StartHeatedToolsAsync(sd =>
        {
            WriteLoggingMacros(sd, 0);
            sd.WriteSys("stop.g", "; keeps the end of the job from switching the heaters off\n");
            sd.WriteGCode("sim.gcode", "T0\nset global.log = global.log ^ \"tool\" ^ state.currentTool ^ \";\"\n");
        });
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M37 P\"0:/gcodes/sim.gcode\" F0");
        await host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tool0;"), "the simulated T0 selected tool 0 and ran none of its macros");
            Assert.That(TemperatureMessages(bench), Is.Empty, "no heater was switched");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Off), "tool 0 was not activated");
        });
    }

    /// <summary>
    /// abort in a tool change macro of a job ends the job and the tool change with it: the old tool stays selected
    /// and active, and the new tool's macros do not run
    /// </summary>
    /// <remarks>
    /// The abort command calls GCodes::AbortPrint, whose AbortStateMachine ends the tool change, and for a file
    /// channel StopPrint and gb.Init() discard the state the T code was in
    /// </remarks>
    [Test]
    public async Task AbortInAToolChangeMacroEndsTheJobsToolChange()
    {
        await using Bench bench = await StartHeatedToolsAsync(sd =>
        {
            WriteLoggingMacros(sd, 0, 1);
            sd.WriteSys("tfree0.g", "set global.log = global.log ^ \"tfree0;\"\nabort \"drt abort\"\nset global.log = global.log ^ \"after;\"\n");
            sd.WriteGCode("job.gcode", "T0\nT1\nset global.log = global.log ^ \"job;\"\n");
        });
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await host.WaitForModelAsync(model => model.Job.LastFileAborted, true, "job.lastFileAborted");
        await host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await host.GlobalTextAsync("log"), Is.EqualTo("tpre0;tpost0;tfree0;"), "nothing ran after the abort");
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "tool 0 is still selected");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.State), Is.EqualTo(ToolState.Active), "tool 0 was not put on standby");
        });
    }
}

