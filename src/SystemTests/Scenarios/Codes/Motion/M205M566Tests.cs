using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M205 and M566, jerk</summary>
[TestFixture]
[GCode("M205", "M566")]
public class M205M566Tests : BenchFixture
{
    /// <summary>RRF's lowest jerk, Configuration.h MinimumJerk (0.1 mm/s), in the mm/min the object model reports</summary>
    private const float RrfMinimumJerkMmPerMin = 0.1f * 60.0f;

    /// <summary>RRF's default jerk limits, Configuration.h DefaultAxisInstantDv, DefaultZInstantDv and DefaultEInstantDv (mm/s)</summary>
    private const float RrfDefaultAxisJerk = 15.0f, RrfDefaultZJerk = 10.0f, RrfDefaultEJerk = 5.0f;

    /// <summary>Drivers of <see cref="Machines.DriverBoard"/> carrying three extruders</summary>
    private static readonly byte[] ExtruderDrivers = [Machines.EDriver, Machines.EDriver + 1, Machines.EDriver + 2];

    /// <summary>Three extruders and nothing else, so an E list can be shorter or longer than the machine has extruders</summary>
    private static readonly string ThreeExtruders =
        string.Join("\n", ExtruderDrivers.Select(driver => $"M569 P{Expansion.Driver(Machines.DriverBoard, driver)} S1"))
        + $"\nM584 E{string.Join(":", ExtruderDrivers.Select(driver => Expansion.Driver(Machines.DriverBoard, driver)))}";

    /// <summary>Feed rate of the moves the planning scenarios run, mm/min</summary>
    private const float PlanningFeedRate = 3000.0f;

    /// <summary>Length of each move the planning scenarios run, mm</summary>
    private const float PlanningMoveLength = 10.0f;

    /// <summary>Filament the printing move of <see cref="TravelThenPrintMacro"/> extrudes, mm</summary>
    private const float PrintingMoveExtrusion = 1.0f;

    /// <summary>A right-angle corner between two travel moves</summary>
    private static readonly string CornerMacro = $"""
        G91
        G1 X{PlanningMoveLength} F{PlanningFeedRate}
        G1 Y{PlanningMoveLength} F{PlanningFeedRate}
        G90
        """;

    /// <summary>A travel move straight into a printing move along the same line, so only the extruder changes speed at the junction</summary>
    private static readonly string TravelThenPrintMacro = $"""
        G91
        M83
        G1 X{PlanningMoveLength} F{PlanningFeedRate}
        G1 X{PlanningMoveLength} E{PrintingMoveExtrusion} F{PlanningFeedRate}
        G90
        """;

    private static string Jerk(float mmPerMin) => mmPerMin.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// M566 (mm/min) sets the machine jerk limit and pulls the printing jerk with it; M205 (mm/s)
    /// sets only the printing jerk and is clamped to the machine limit
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::SetInstantDv: includingMax (M566) writes both arrays, otherwise (M205)
    /// printing = min(value, max). move.axes[].jerk and .printingJerk are reported in mm/min
    /// (Move.cpp axes table, InverseConvertSpeedToMmPerMin)
    /// </remarks>
    [Test]
    public async Task M205AndM566SetJerk()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Axis axis = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        await bench.Host.ExecuteCodeAsync("M566 X1200 E300");
        Assert.Multiple(() =>
        {
            Assert.That(axis.Jerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].jerk (mm/min, RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(axis.PrintingJerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].printingJerk as well (RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(extruder.Jerk, Is.EqualTo(300.0).Within(1e-2),
                        "M566 E sets move.extruders[0].jerk (mm/min, RRF Move2.cpp SetInstantDv)");
        });

        await bench.Host.ExecuteCodeAsync("M205 X5");
        Assert.Multiple(() =>
        {
            Assert.That(axis.PrintingJerk, Is.EqualTo(300.0).Within(1e-2),
                        "M205 X5 is 5 mm/s, so move.axes[0].printingJerk becomes 300 mm/min (RRF GCodes2.cpp case 205)");
            Assert.That(axis.Jerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M205 leaves the machine limit move.axes[0].jerk alone (RRF Move2.cpp SetInstantDv, not includingMax)");
        });

        await bench.Host.ExecuteCodeAsync("M566 X1500");
        Assert.That(axis.PrintingJerk, Is.EqualTo(1500.0).Within(1e-2),
                    "a later M566 overwrites the lower printing jerk M205 set, rather than keeping the lower of the two (RRF Move2.cpp SetInstantDv includingMax)");
    }

    /// <summary>
    /// Before any M566 the axes and extruders have RRF's default jerk limits
    /// </summary>
    /// <remarks>RRF Move.cpp Move::Init and the regression references' baseline: X and Y 900, Z 600, a new extruder 300 mm/min</remarks>
    [Test]
    public async Task UnconfiguredDrivesHaveTheDefaultJerk()
    {
        await using Bench bench = await Bench.StartAsync(ThreeExtruders);

        Move move = await bench.Host.ReadModelAsync(model => model.Move);
        Assert.Multiple(() =>
        {
            foreach (Axis axis in move.Axes)
            {
                float expected = (axis.Letter == 'Z' ? RrfDefaultZJerk : RrfDefaultAxisJerk) * 60.0f;
                Assert.That(axis.Jerk, Is.EqualTo(expected).Within(1e-2), $"default {axis.Letter} jerk");
                Assert.That(axis.PrintingJerk, Is.EqualTo(expected).Within(1e-2), $"default {axis.Letter} printing jerk");
            }
            foreach (Extruder extruder in move.Extruders)
            {
                Assert.That(extruder.Jerk, Is.EqualTo(RrfDefaultEJerk * 60.0f).Within(1e-2), "default extruder jerk");
                Assert.That(extruder.PrintingJerk, Is.EqualTo(RrfDefaultEJerk * 60.0f).Within(1e-2), "default extruder printing jerk");
            }
        });
    }

    /// <summary>
    /// Each axis letter sets that axis alone, in mm/min, and nothing goes to the expansion boards
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 566 loops over every axis letter; jerk only reaches the boards through the
    /// planned move speeds, and the m566-set-jerk reference records no CAN traffic for the code
    /// </remarks>
    [Test]
    public async Task M566SetsOnlyTheAxesItNames()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze);
        Move move = await bench.Host.ReadModelAsync(model => model.Move);

        const float x = 700.0f, y = 800.0f, z = 90.0f, ySecond = 850.0f;
        string reply = await bench.Host.ExecuteCodeAsync($"M566 X{x} Y{y} Z{z}");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "setting jerk is not a report");
            Assert.That(move.Axes[0].Jerk, Is.EqualTo(x).Within(1e-2));
            Assert.That(move.Axes[1].Jerk, Is.EqualTo(y).Within(1e-2));
            Assert.That(move.Axes[2].Jerk, Is.EqualTo(z).Within(1e-2));
            Assert.That(move.Axes[2].PrintingJerk, Is.EqualTo(z).Within(1e-2));
            Assert.That(move.Extruders[0].Jerk, Is.EqualTo(Machines.EJerk * 60.0f).Within(1e-2), "no E, so the extruder keeps its jerk");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty,
                        "jerk is the planner's limit, so M566 tells no board anything");
        });

        await bench.Host.ExecuteCodeAsync($"M566 Y{ySecond}");
        Assert.Multiple(() =>
        {
            Assert.That(move.Axes[1].Jerk, Is.EqualTo(ySecond).Within(1e-2));
            Assert.That(move.Axes[0].Jerk, Is.EqualTo(x).Within(1e-2), "X was not named, so it keeps its jerk");
            Assert.That(move.Axes[2].Jerk, Is.EqualTo(z).Within(1e-2), "Z was not named, so it keeps its jerk");
        });
    }

    /// <summary>
    /// An E list sets the extruders positionally, a single value applies to every extruder, and a
    /// shorter list leaves the extruders it does not reach alone
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 566: GetFloatArray(eVals, eCount, true) pads a single value; a shorter list sets eCount extruders</remarks>
    [TestCase("M566 E700:800:900", new[] { 700.0f, 800.0f, 900.0f })]
    [TestCase("M566 E700:800", new[] { 700.0f, 800.0f, 600.0f })]
    [TestCase("M566 E700", new[] { 700.0f, 700.0f, 700.0f })]
    public async Task M566SetsExtrudersFromTheEList(string code, float[] expected)
    {
        await using Bench bench = await Bench.StartAsync(ThreeExtruders, "M566 E400:500:600");
        Move move = await bench.Host.ReadModelAsync(model => model.Move);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "an extruder-only M566 reports nothing (RRF reports only when no axis, E or P is seen)");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(move.Extruders[i].Jerk, Is.EqualTo(expected[i]).Within(1e-2), $"move.extruders[{i}].jerk");
                Assert.That(move.Extruders[i].PrintingJerk, Is.EqualTo(expected[i]).Within(1e-2), $"move.extruders[{i}].printingJerk");
            }
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M566")).TrimEnd(),
                    Does.EndWith($"E: {string.Join(":", expected.Select(Jerk))}, jerk policy: 0"),
                    "the report lists every extruder, colon separated (RRF GCodes2.cpp case 566)");
    }

    /// <summary>
    /// An E list longer than the machine has extruders refuses the code and changes no extruder
    /// </summary>
    /// <remarks>RRF StringParser::CheckArrayLength throws before SetInstantDv is reached</remarks>
    [Test]
    public async Task M566RefusesMoreExtruderValuesThanExtruders()
    {
        await using Bench bench = await Bench.StartAsync(ThreeExtruders, "M566 E400:500:600");
        Move move = await bench.Host.ReadModelAsync(model => model.Move);

        string reply = await bench.Host.ExecuteCodeAsync("M566 E700:800:900:1000");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(move.Extruders.Select(extruder => extruder.Jerk), Is.EqualTo(new[] { 400.0f, 500.0f, 600.0f }).Within(1e-2f),
                        "no extruder is set, including the ones the list did reach");
        });
    }

    /// <summary>
    /// A jerk below 0.1 mm/s, zero or negative included, is raised to 0.1 mm/s rather than refused
    /// </summary>
    /// <remarks>RRF Move2.cpp SetInstantDv: max(value, MinimumJerk), applied to axes and extruders alike</remarks>
    [TestCase('X', "0")]
    [TestCase('X', "3")]
    [TestCase('X', "-100")]
    [TestCase('E', "0")]
    public async Task M566ClampsJerkToTheMinimum(char letter, string value)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        string reply = await bench.Host.ExecuteCodeAsync($"M566 {letter}{value}");
        (float jerk, float printingJerk) = await bench.Host.ReadModelAsync(model => letter == 'E'
            ? (model.Move.Extruders[0].Jerk, model.Move.Extruders[0].PrintingJerk)
            : (model.Move.Axes[0].Jerk, model.Move.Axes[0].PrintingJerk));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error").And.Not.StartWith("Warning"), "a low value is clamped, not refused");
            Assert.That(jerk, Is.EqualTo(RrfMinimumJerkMmPerMin).Within(1e-2));
            Assert.That(printingJerk, Is.EqualTo(RrfMinimumJerkMmPerMin).Within(1e-2));
        });
    }

    /// <summary>A negative extruder jerk refuses the code and leaves the extruder's jerk as it was</summary>
    /// <remarks>RRF GCodes2.cpp case 566 clamps it to the minimum instead; see rrf-differences.md section 3.1</remarks>
    [Test]
    public async Task M566RefusesANegativeExtruderJerk()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        Extruder before = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        string reply = await bench.Host.ExecuteCodeAsync("M566 E-100");
        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(extruder.Jerk, Is.EqualTo(before.Jerk));
            Assert.That(extruder.PrintingJerk, Is.EqualTo(before.PrintingJerk));
        });
    }

    /// <summary>
    /// M566 is in mm/min whatever G20/G21 says: the jerk is not scaled by inches
    /// </summary>
    /// <remarks>RRF GCodeBuffer::GetSpeedFromMm reads GetFValue with no distance-unit conversion</remarks>
    [Test]
    public async Task M566IgnoresInchUnits()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        const float jerk = 1200.0f;
        await bench.Host.ExecuteCodeAsync("G20");
        await bench.Host.ExecuteCodeAsync($"M566 X{jerk}");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Jerk), Is.EqualTo(jerk).Within(1e-2));
    }

    /// <summary>
    /// A value M566 cannot read refuses the code and leaves the setting as it was
    /// </summary>
    /// <remarks>RRF StringParser::ReadFloatValue throws "expected number" for a bare letter</remarks>
    [TestCase("M566 X")]
    [TestCase("M566 E")]
    public async Task M566RefusesValuesItCannotRead(string code)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, "M566 P1");
        Move move = await bench.Host.ReadModelAsync(model => model.Move);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(move.Axes[0].Jerk, Is.EqualTo(Machines.XyJerk * 60.0f).Within(1e-2));
            Assert.That(move.Extruders[0].Jerk, Is.EqualTo(Machines.EJerk * 60.0f).Within(1e-2));
            Assert.That(move.JerkPolicy, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A negative jerk policy is refused and the policy stays as it was
    /// </summary>
    /// <remarks>RRF reads P with GetUIValue (StringParser::ReadUIValue, unsigned decimal only), which throws "expected number" on a minus sign</remarks>
    [Test]
    public async Task M566RefusesANegativeJerkPolicy()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, "M566 P1");

        string reply = await bench.Host.ExecuteCodeAsync("M566 P-1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.JerkPolicy), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// M566 P sets the jerk policy without touching any jerk, and M205 has no P
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 566: P is read only when code == 566 and counts as a setting, so it
    /// suppresses the report. RRF keeps the policy out of its object model; DSF reports it as move.jerkPolicy
    /// </remarks>
    [Test]
    public async Task M566PSetsTheJerkPolicy()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        Move move = await bench.Host.ReadModelAsync(model => model.Move);

        Assert.That(move.JerkPolicy, Is.Zero, "the default policy is 0 (RRF Move.cpp constructor)");

        string reply = await bench.Host.ExecuteCodeAsync("M566 P1");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "P is a setting, not a report");
            Assert.That(move.JerkPolicy, Is.EqualTo(1));
            Assert.That(move.Axes[0].Jerk, Is.EqualTo(Machines.XyJerk * 60.0f).Within(1e-2), "P alone sets no jerk");
            Assert.That(move.Extruders[0].Jerk, Is.EqualTo(Machines.EJerk * 60.0f).Within(1e-2), "P alone sets no jerk");
        });

        await bench.Host.ExecuteCodeAsync("M566 P0");
        Assert.That(move.JerkPolicy, Is.Zero);

        await bench.Host.ExecuteCodeAsync("M205 P1");
        Assert.That(move.JerkPolicy, Is.Zero, "M205 does not read P");
    }

    /// <summary>
    /// M566 with no parameters reports the machine jerk of every axis and extruder in mm/min and the jerk policy
    /// </summary>
    /// <remarks>
    /// The m566-report-jerk and m566-jerk-policy references:
    /// "Maximum jerk rates (mm/min): X: 600.0, Y: 600.0, Z: 60.0, E: 300.0, jerk policy: 1".
    /// M205 lowers only the printing jerk, so it does not show here
    /// </remarks>
    [Test]
    public async Task M566ReportsMachineJerkAndPolicy()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze);

        string expectedJerks = $"X: {Jerk(Machines.XyJerk * 60)}, Y: {Jerk(Machines.XyJerk * 60)}, Z: {Jerk(Machines.ZJerk * 60)}, E: {Jerk(Machines.EJerk * 60)}";

        await bench.Host.ExecuteCodeAsync("M205 X1 Y1");
        Assert.That((await bench.Host.ExecuteCodeAsync("M566")).TrimEnd(),
                    Is.EqualTo($"Maximum jerk rates (mm/min): {expectedJerks}, jerk policy: 0"));

        await bench.Host.ExecuteCodeAsync("M566 P1");
        Assert.That((await bench.Host.ExecuteCodeAsync("M566")).TrimEnd(),
                    Is.EqualTo($"Maximum jerk rates (mm/min): {expectedJerks}, jerk policy: 1"));
    }

    /// <summary>
    /// The speed at a right-angle corner is the printing jerk: M566 sets it, and an M205 below the
    /// machine limit lowers it further
    /// </summary>
    /// <remarks>
    /// RRF DDA_3rdOrder.cpp SetSpeedRatioAndMaxJunctionSpeedForNonPrintingMoves limits the junction
    /// by the machine jerk, then DDA::MatchSpeeds by the printing jerk; at a right angle each axis
    /// changes speed by the whole junction speed
    /// </remarks>
    [TestCase(300.0f, null)]
    [TestCase(1200.0f, null)]
    [TestCase(1200.0f, 5.0f)]
    public async Task CornerSpeedIsTheJerkLimit(float machineJerkMmPerMin, float? printingJerkMmPerSec)
    {
        await using Bench bench = await Bench.With(Machines.Xye)
                                             .WithSd(sd => sd.WriteMacro("corner.g", CornerMacro))
                                             .StartRelativeAsync();

        await bench.Host.ExecuteCodeAsync($"M566 X{machineJerkMmPerMin} Y{machineJerkMmPerMin}");
        if (printingJerkMmPerSec is float printing)
        {
            await bench.Host.ExecuteCodeAsync($"M205 X{printing} Y{printing}");
        }
        float expected = printingJerkMmPerSec ?? machineJerkMmPerMin / 60.0f;

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("M98 P\"0:/macros/corner.g\"");
        Assert.That(moves, Has.Count.EqualTo(2), "one move per G1");
        Assert.Multiple(() =>
        {
            Assert.That(MoveCapture.MmPerSecond(moves[0].Header.EndSpeed), Is.EqualTo(expected).Within(0.01f),
                        "the first move ends at the jerk limit");
            Assert.That(MoveCapture.MmPerSecond(moves[1].Header.StartSpeed), Is.EqualTo(expected).Within(0.01f),
                        "and the second starts at it");
        });
    }

    /// <summary>
    /// The axis jerks M566 read before refusing its E list stand, and the planner uses them
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 566 calls SetInstantDv for each axis before GetFloatArray throws on an E
    /// list longer than there are extruders, so the axes keep their new values
    /// </remarks>
    [Test]
    public async Task AxisJerksReadBeforeARefusedExtruderListAreUsed()
    {
        const float machineJerkMmPerMin = 300.0f;
        await using Bench bench = await Bench.With(Machines.Xye)
                                             .WithSd(sd => sd.WriteMacro("corner.g", CornerMacro))
                                             .StartRelativeAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M566 X{machineJerkMmPerMin} Y{machineJerkMmPerMin} E1:2");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "Machines.Xye has one extruder");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Jerk), Is.EqualTo(machineJerkMmPerMin).Within(1e-2));
        });

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("M98 P\"0:/macros/corner.g\"");
        Assert.That(MoveCapture.MmPerSecond(moves[0].Header.EndSpeed), Is.EqualTo(machineJerkMmPerMin / 60.0f).Within(0.01f),
                    "the corner is taken at the new jerk");
    }

    /// <summary>
    /// Jerk policy 0 stops between a travel move and a printing move; policy 1 runs straight through
    /// </summary>
    /// <remarks>
    /// RRF DDA.cpp DDA::InitStandardMove: melding needs GetJerkPolicy() != 0 or both moves of the
    /// same type (isPrintingMove, xyMoving, isNonPrintingExtruderMove). The extruder jerk is set high
    /// so that it does not limit the junction under policy 1
    /// </remarks>
    [TestCase(0)]
    [TestCase(1)]
    public async Task JerkPolicyDecidesWhetherTravelMeldsIntoPrinting(int policy)
    {
        await using Bench bench = await Bench.With(Machines.Xye, Machines.OneTool)
                                             .WithSd(sd => sd.WriteMacro("travel-print.g", TravelThenPrintMacro))
                                             .StartRelativeAsync();

        await bench.Host.ExecuteCodeAsync($"M566 P{policy} E{PlanningFeedRate}");
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("M98 P\"0:/macros/travel-print.g\"");

        Assert.That(moves, Has.Count.EqualTo(2), "one move per G1");
        float junction = MoveCapture.MmPerSecond(moves[0].Header.EndSpeed);
        if (policy == 0)
        {
            Assert.That(junction, Is.Zero, "policy 0 does not meld a travel move into a printing move, so the machine stops between them");
        }
        else
        {
            Assert.That(junction, Is.EqualTo(PlanningFeedRate / 60.0f).Within(0.01f),
                        "policy 1 melds them, and only the extruder changes speed, within its jerk, so the junction runs at the feed rate");
        }
    }

    /// <summary>
    /// Where extrusion starts the extruder's jerk limits the junction speed, so a lower M566 E gives a slower junction
    /// </summary>
    /// <remarks>
    /// RRF DDA::MatchSpeeds checks every drive including extruders: the extruder's share of the
    /// speed goes from 0 to E/X, so the junction is at most the E jerk divided by E/X
    /// </remarks>
    [Test]
    public async Task ExtruderJerkLimitsTheJunctionWhereExtrusionStarts()
    {
        await using Bench bench = await Bench.With(Machines.Xye, Machines.OneTool)
                                             .WithSd(sd => sd.WriteMacro("travel-print.g", TravelThenPrintMacro))
                                             .StartRelativeAsync();

        await bench.Host.ExecuteCodeAsync("M566 P1");
        float lowJunction = MoveCapture.MmPerSecond((await bench.RunMoveAsync("M98 P\"0:/macros/travel-print.g\""))[0].Header.EndSpeed);

        await bench.Host.ExecuteCodeAsync($"M566 E{PlanningFeedRate}");
        float highJunction = MoveCapture.MmPerSecond((await bench.RunMoveAsync("M98 P\"0:/macros/travel-print.g\""))[0].Header.EndSpeed);

        Assert.Multiple(() =>
        {
            Assert.That(lowJunction, Is.EqualTo(Machines.EJerk / (PrintingMoveExtrusion / PlanningMoveLength)).Within(0.05f),
                        "the extruder jumps from rest to E/X of the junction speed, which its jerk caps");
            Assert.That(lowJunction, Is.LessThan(highJunction), "a higher extruder jerk allows a faster junction");
        });
    }
}
