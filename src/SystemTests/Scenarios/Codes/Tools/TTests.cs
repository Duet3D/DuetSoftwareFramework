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

    /// <summary>
    /// T R1 selects the tool recorded in restore point 1: with no pause recorded that tool number
    /// is -1, so T R1 deselects the current tool
    /// </summary>
    /// <remarks>
    /// GCodes::HandleTcode (GCodes2.cpp) reads restorePoints[R].toolNumber; RestorePoint::Init
    /// (RestorePoint.cpp) starts toolNumber at -1
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task TR1SelectsRestorePointTool()
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"OnlyTool\"\n");
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("T0");
        Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "T0 selected the tool");

        await host.ExecuteCodeAsync("T R1");
        Assert.Multiple(async () =>
        {
#pragma warning disable CS0618
            Assert.That(await EvaluateNumberAsync(host, model => model.State.RestorePoints[1].ToolNumber), Is.EqualTo(-1),
                        "no pause has recorded a tool (RRF RestorePoint::Init)");
#pragma warning restore
            Assert.That(await host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1),
                        "T R1 selects restorePoints[1].toolNumber, which is -1 (RRF GCodes::HandleTcode)");
        });
    }
}
