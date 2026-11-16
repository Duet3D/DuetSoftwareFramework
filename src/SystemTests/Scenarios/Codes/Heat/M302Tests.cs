using System;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static System.FormattableString;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>
/// M302, cold extrusion, asserted against RepRapFirmware's GCodes2.cpp case 302, Heat.cpp's objectModelTable,
/// Tool::ExtruderMovementAllowed and DDA::Prepare, and against the regression suite's m302-allow-cold-extrudes reference
/// </summary>
[TestFixture]
[GCode("M302")]
public class M302Tests : BenchFixture
{
    /// <summary>RRF3Common.h DefaultMinExtrusionTemperature, C</summary>
    private const float DefaultMinExtrusionTemperature = 160.0f;

    /// <summary>RRF3Common.h DefaultMinRetractionTemperature, C</summary>
    private const float DefaultMinRetractionTemperature = 90.0f;

    /// <summary>The heater of the hot end tool 0 drives</summary>
    private const int HotEndHeater = 1;

    /// <summary>How far the X axis travels in the extruding moves, mm</summary>
    private const float MoveDistance = 10.0f;

    /// <summary>Long enough for a code that is not waiting to have finished many times over</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>Tool 0 driving extruder 0 and heated by <see cref="HotEndHeater"/> on board 1, selected</summary>
    private static readonly string HotEnd = $"""
        M308 S{HotEndHeater} P"{Expansion.Temp(Machines.DriverBoard, HotEndHeater)}" Y"thermistor"
        M950 H{HotEndHeater} C"{Expansion.Out(Machines.DriverBoard, HotEndHeater)}" T{HotEndHeater}
        M563 P0 D0 H{HotEndHeater}
        T0
        """;

    /// <summary>Read both minimum temperatures the object model reports</summary>
    private static Task<(float Extrude, float Retract)> MinimumsAsync(Bench bench)
        => bench.Host.ReadModelAsync(model => (model.Heat.ColdExtrudeTemperature, model.Heat.ColdRetractTemperature));

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

    /// <summary>Cold extrusion starts forbidden at the default minimums, and the bare report is not an error</summary>
    /// <remarks>RRF truth: Heat::Init. Regression: m302-allow-cold-extrudes, whose first M302 reports 160C and 90C</remarks>
    [Test]
    public async Task ColdExtrusionStartsForbidden()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That(await MinimumsAsync(bench), Is.EqualTo((DefaultMinExtrusionTemperature, DefaultMinRetractionTemperature)),
                    "heat.coldExtrudeTemperature and coldRetractTemperature start at RRF's defaults");
        Assert.That(await bench.Host.ExecuteCodeAsync("M302"), Is.Not.Empty.And.Not.StartWith("Error").And.Not.StartWith("Warning"),
                    "bare M302 reports the state as a plain reply");
    }

    /// <summary>
    /// P allows cold extrusion when greater than 0 and forbids it otherwise; S and R given while it is allowed are kept
    /// behind the 0s the model reports, and P0 brings them back rather than resetting them
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 302 calls AllowColdExtrude(GetIValue() > 0) and keeps extrusionMinTemp and
    /// retractionMinTemp apart from the coldExtrude flag, which Heat.cpp's objectModelTable reports as 0 while set
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task AllowingHidesTheMinimumsWithoutLosingThem()
    {
        await using Bench bench = await Bench.StartAsync("M302 S120 R110");

        await bench.Host.ExecuteCodeAsync("M302 P2");
        Assert.That(await MinimumsAsync(bench), Is.EqualTo((0.0f, 0.0f)), "any P above 0 allows cold extrusion");

        await bench.Host.ExecuteCodeAsync("M302 S130 R100");
        Assert.That(await MinimumsAsync(bench), Is.EqualTo((0.0f, 0.0f)), "S and R do not forbid cold extrusion again");

        await bench.Host.ExecuteCodeAsync("M302 P-1");
        Assert.That(await MinimumsAsync(bench), Is.EqualTo((130.0f, 100.0f)),
                    "P at or below 0 forbids it, and the minimums set while it was allowed come back");
    }

    /// <summary>P, S or R without a value is an error and changes nothing</summary>
    /// <remarks>RRF truth: StringParser::ReadIValue and ReadFloatValue throw from CheckNumberFound</remarks>
    [TestCase("M302 P")]
    [TestCase("M302 S")]
    [TestCase("M302 R")]
    public async Task RefusesAParameterWithoutAValue(string code)
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync(code), Does.StartWith("Error"), $"{code} is refused");
        Assert.That(await MinimumsAsync(bench), Is.EqualTo((DefaultMinExtrusionTemperature, DefaultMinRetractionTemperature)),
                    $"{code} leaves the minimums alone");
    }

    /// <summary>
    /// Changing the interlock waits for the moves already queued to finish, so that it applies from the next move on
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 302 calls LockAllMovementSystemsAndWaitForStandstill for each of P, S and R</remarks>
    [TestCase("M302 P0")]
    [TestCase("M302 S120")]
    [TestCase("M302 R110")]
    [Category("KnownGap")]
    public async Task ChangingTheInterlockWaitsForStandstill(string code)
        => Assert.That(await AnswersWhileAMoveIsQueuedAsync(code), Is.False, $"{code} waits for the X move, the timeline being frozen");

    /// <summary>The bare report changes nothing, so it does not wait for standstill</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 302 locks movement only for P, S and R</remarks>
    [Test]
    public async Task TheReportDoesNotWaitForStandstill()
        => Assert.That(await AnswersWhileAMoveIsQueuedAsync("M302"), Is.True, "M302 answers without waiting for the X move");

    /// <summary>Whether the code finishes while an X move is held queued by a frozen timeline</summary>
    private static async Task<bool> AnswersWhileAMoveIsQueuedAsync(string code)
    {
        using SteppedTimeline timeline = new();
        await using Bench bench = await Bench.With(Machines.Xye).SteppedBy(timeline).StartAsync();
        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("G90"));
        await bench.Host.ExecuteCodeAsync(Invariant($"G1 X{MoveDistance} F600"));

        Task<string> m302 = bench.Host.ExecuteCodeAsync(code);
        await Task.Delay(Settle);
        bool answered = m302.IsCompleted;
        await timeline.WhileRunningAsync(() => m302);
        return answered;
    }

    /// <summary>
    /// With cold extrusion forbidden, the extruder is left out of a move unless every heater of the tool is at least at
    /// the minimum for the direction: the extrusion minimum going forwards, the retraction minimum backwards. The axes
    /// still move, the move itself is not refused, and a warning follows
    /// </summary>
    /// <remarks>
    /// RRF truth: DDA::Prepare skips an extruder Tool::ExtruderMovementAllowed refuses; Tool::CanDriveExtruder checks
    /// AllHeatersAtHighTemperature against GetExtrusionMinTemp or GetRetractionMinTemp and flags the tool, which
    /// RepRap::Spin reports through Tool::DisplayColdExtrusionWarnings as a WarningMessage
    /// </remarks>
    [TestCase("M302 P0", 25.0f, 1.0f, TestName = "M302 P0 drops a cold extrusion")]
    [TestCase("M302 P0", 25.0f, -1.0f, TestName = "M302 P0 drops a cold retraction")]
    [TestCase("M302 P0", 120.0f, 1.0f, TestName = "M302 P0 drops an extrusion below the extrusion minimum")]
    [TestCase("M302 P0 S100 R50", 40.0f, -1.0f, TestName = "M302 R raises the retraction minimum")]
    [Category("KnownGap")]
    public async Task TheExtruderIsLeftOutBelowTheMinimum(string m302, float temperature, float extrusion)
    {
        await using Bench bench = await ExtrudeAsync(m302, temperature, extrusion);
        Assert.That(bench.CanMaster.ScheduledExtrusion(Machines.EDriver), Is.Zero.Within(1e-2f), "the extruder is left out of the move");
        await bench.Host.WaitForMessageAsync(MessageType.Warning, string.Empty);
    }

    /// <summary>
    /// The extruder is driven when every heater of the tool is at least at the minimum for the direction, or when cold
    /// extrusion is allowed
    /// </summary>
    /// <remarks>RRF truth: Tool::ExtruderMovementAllowed and Tool::AllHeatersAtHighTemperature</remarks>
    [TestCase("M302 P0", 120.0f, -1.0f, TestName = "M302 P0 drives a retraction above the retraction minimum")]
    [TestCase("M302 P0", 200.0f, 1.0f, TestName = "M302 P0 drives an extrusion above the extrusion minimum")]
    [TestCase("M302 P1", 25.0f, 1.0f, TestName = "M302 P1 drives a cold extrusion")]
    [TestCase("M302 P1", 25.0f, -1.0f, TestName = "M302 P1 drives a cold retraction")]
    [TestCase("M302 P0 S100 R50", 120.0f, 1.0f, TestName = "M302 S lowers the extrusion minimum")]
    public async Task TheExtruderIsDrivenAtTheMinimum(string m302, float temperature, float extrusion)
    {
        await using Bench bench = await ExtrudeAsync(m302, temperature, extrusion);
        Assert.That(bench.CanMaster.ScheduledExtrusion(Machines.EDriver), Is.EqualTo(extrusion * Machines.EStepsPerMm).Within(1e-2f),
                    "the extruder is driven");
    }

    /// <summary>
    /// With the hot end's heater at the given temperature, run the M302 line and a relative X move extruding the given
    /// amount, asserting the parts that hold whether or not the extruder is driven
    /// </summary>
    private static async Task<Bench> ExtrudeAsync(string m302, float temperature, float extrusion)
    {
        Bench bench = await Bench.StartAsync(Machines.Xye, HotEnd);
        try
        {
            await bench.ReportHeaterAsync(HotEndHeater, HeaterMode.Off, temperature);
            await bench.Host.ExecuteCodeAsync(m302);
            await bench.Host.ExecuteCodeAsync("M83");

            string reply = await bench.Host.ExecuteCodeAsync(Invariant($"G1 X{MoveDistance} E{extrusion} F600"));
            await bench.Host.ExecuteCodeAsync("M400");
            Assert.Multiple(() =>
            {
                Assert.That(reply.Trim(), Is.Empty, "the move is not refused");
                Assert.That(bench.CanMaster.ScheduledSteps(Machines.XDriver), Is.EqualTo(MoveDistance * Machines.XyStepsPerMm),
                            "the axes move either way");
            });
            return bench;
        }
        catch
        {
            await bench.DisposeAsync();
            throw;
        }
    }

    /// <summary>A tool without a heater is not protected, so its extruder is driven with cold extrusion forbidden</summary>
    /// <remarks>RRF truth: Tool::AllHeatersAtHighTemperature holds for a tool with no heaters; the wiki's M302 notes say the same</remarks>
    [Test]
    public async Task AToolWithoutAHeaterIsNotProtected()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, Machines.OneTool, "M302 P0");
        await bench.Host.ExecuteCodeAsync("M83");

        await bench.Host.ExecuteCodeAsync(Invariant($"G1 X{MoveDistance} E1 F600"));
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(bench.CanMaster.ScheduledExtrusion(Machines.EDriver), Is.EqualTo(Machines.EStepsPerMm).Within(1e-2f),
                    "the 1 mm extrusion is driven");
    }
}
