using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M572, pressure advance</summary>
[TestFixture]
[GCode("M572")]
public class M572Tests : BenchFixture
{
    /// <summary>
    /// Two extruders, extruder 0 on the single-driver board and extruder 1 on the six-driver one,
    /// as <c>testcases/tools/m572-set-pressure-advance.yaml</c> maps them
    /// </summary>
    private static readonly string TwoExtruders = $"M584 E{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)}";

    /// <summary>
    /// M572 sets the pressure advance. A single S value is the classic coefficient; two values with
    /// L set the second coefficient and its transition
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::ConfigurePressureAdvance and ExtruderShaper.cpp object model table:
    /// move.extruders[].pressureAdvance reports k0 in seconds, move.extruders[].pressAdv holds k0,
    /// k1 and d. With one S value RRF copies k0 into k1 and sets d to infinity, which the object
    /// model reports as null
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572SetsPressureAdvance()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.05");
        Assert.Multiple(() =>
        {
#pragma warning disable CS0618
            Assert.That(extruder.PressureAdvance, Is.EqualTo(0.05).Within(1e-6),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
#pragma warning restore
            Assert.That(extruder.PressAdv.K0, Is.EqualTo(0.05).Within(1e-6),
                        "M572 S sets move.extruders[0].pressAdv.k0 in seconds, as given rather than rounded to the half precision the CAN message carries");
            Assert.That(extruder.PressAdv.K1, Is.EqualTo(0.05).Within(1e-6),
                        "a single M572 S value copies k0 into move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.D, Is.Null,
                        "and leaves move.extruders[0].pressAdv.d null, single-slope pressure advance");
        });

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.06:0.08 L2");
        Assert.Multiple(() =>
        {
#pragma warning disable CS0618
            Assert.That(extruder.PressureAdvance, Is.EqualTo(0.06).Within(1e-6),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
#pragma warning restore
            Assert.That(extruder.PressAdv.K0, Is.EqualTo(0.06).Within(1e-6),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k0 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.K1, Is.EqualTo(0.08).Within(1e-6),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.D, Is.EqualTo(2.0).Within(1e-6),
                        "M572 L sets move.extruders[0].pressAdv.d (RRF Move2.cpp ConfigurePressureAdvance dk)");
        });
    }

    /// <summary>
    /// Each form reaches the extruder's board in a setPressureAdvanceV2 carrying both coefficients
    /// and the distance, and the report lists every extruder whatever D says, a two-slope one in
    /// brackets
    /// </summary>
    /// <remarks>
    /// <c>testcases/tools/m572-set-pressure-advance.yaml</c>, whose reference records the single
    /// value as k0 = k1 = 0.1 with dk = +infinity (0x7c00) and the two-slope form as 0.9, 0.1 and 5,
    /// all in half precision (CANlib ShortPressureAdvanceParameters). The report is
    /// ExtruderShaper::AppendParameters for each extruder
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572SendsBothFormsToTheExtrudersBoardAndReportsThem()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, TwoExtruders, "M572 D0:1 S0");

        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0 S0.1")).Trim(), Is.Empty);
        (byte board, CanMessageMultipleDrivesRequestShortPressureAdvanceParameters sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>();
        Assert.Multiple(() =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>(), Has.Count.EqualTo(1));
            Assert.That(board, Is.EqualTo(DriversRig.ClosedLoopBoard), "extruder 0's driver is on the single-driver board");
            Assert.That(sent.DriversToUpdate, Is.EqualTo(1 << 0));
            Assert.That(sent.Values[0].K[0], Is.EqualTo((Half)0.1f));
            Assert.That(sent.Values[0].K[1], Is.EqualTo((Half)0.1f), "the single value is both coefficients");
            Assert.That(sent.Values[0].Dk, Is.EqualTo(Half.PositiveInfinity), "and the distance is infinite, so k1 never takes over");
            Assert.That(bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>()[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestShortPressureAdvanceParameters { DriversToUpdate = 0b1 }.GetActualDataLength()),
                        "carrying one driver's parameters and stopping there");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0")).TrimEnd(),
                    Is.EqualTo("Extruder pressure advance: 0.100, 0.000"),
                    "D is ignored by the report, which lists every extruder");

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0 S0.9:0.1 L5.0")).Trim(), Is.Empty);
        (board, sent) = bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>();
        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent.Values[0].K[0], Is.EqualTo((Half)0.9f));
            Assert.That(sent.Values[0].K[1], Is.EqualTo((Half)0.1f));
            Assert.That(sent.Values[0].Dk, Is.EqualTo((Half)5.0f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.D), Is.EqualTo(5.0f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].PressAdv.K0), Is.Zero,
                        "extruder 1 was not named");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0")).TrimEnd(),
                    Is.EqualTo("Extruder pressure advance: (0.900 0.100 5.00), 0.000"));
    }

    /// <summary>
    /// Extruders on the same board share one message, and boards are told in ascending order of
    /// address
    /// </summary>
    /// <remarks>
    /// RRF Move::ConfigurePressureAdvance collects the remote drivers in a CanDriversData, which
    /// keeps them sorted by driver id, and SetRemoteDriverValues sends one message per board
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572SendsOneMessagePerBoard()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config,
            $"M584 E{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(4)}:{DriversRig.MainDriver(3)}");

        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0:1:2 S0.05")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>();
        Assert.That(sent.Select(m => (m.Board, (int)m.Message.DriversToUpdate)),
                    Is.EqualTo(new[] { (DriversRig.MainDriverBoard, (1 << 3) | (1 << 4)), (DriversRig.ClosedLoopBoard, 1 << 0) }),
                    "the six-driver board first, carrying both of its extruders, then the other");
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Message.Values[0].K[0], Is.EqualTo((Half)0.05f));
            Assert.That(sent[0].Message.Values[1].K[0], Is.EqualTo((Half)0.05f));
            Assert.That(sent[0].Message.Values[1].Dk, Is.EqualTo(Half.PositiveInfinity));
        });
    }

    /// <summary>
    /// Without D, M572 sets the extruders of the selected tool, and with no tool selected it is an
    /// error that changes nothing
    /// </summary>
    /// <remarks>
    /// RRF Move::ConfigurePressureAdvance iterates the current tool's extruders, or replies "No tool
    /// selected"; <c>testcases/tools/error-missing-parameters-tools.yaml</c> records that reply for
    /// <c>M572 S0.1</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572WithoutDSetsTheSelectedToolsExtruders()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, TwoExtruders, "M563 P0 D0:1");

        string reply = await bench.Host.ExecuteCodeAsync("M572 S0.1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("No tool selected"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K0), Is.Zero);
        });

        await bench.Host.ExecuteCodeAsync("T0");
        Assert.That((await bench.Host.ExecuteCodeAsync("M572 S0.1")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K0), Is.EqualTo(0.1f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].PressAdv.K0), Is.EqualTo(0.1f));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>().Select(m => m.Board),
                        Is.EqualTo(new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }),
                        "both of the tool's extruders, on their two boards");
        });
    }

    /// <summary>
    /// Every malformed form is refused before anything is changed or sent
    /// </summary>
    /// <remarks>
    /// RRF Move::ConfigurePressureAdvance: a negative coefficient is "pressure advance values must be
    /// non-negative", two values need L ("missing parameter 'L'", which
    /// <c>error-missing-parameters-tools.yaml</c> records for <c>M572 S0.9:0.1</c>) read with
    /// GetNonNegativeFValue, and S holds at most two values. An extruder number past the last is
    /// "Invalid extruder number"; RRF applies the extruders listed before it first, where this side
    /// refuses the whole list (rrf-differences.md, array element limits)
    /// </remarks>
    [TestCase("M572 D0 S-0.1")]
    [TestCase("M572 D0 S0.1:-0.1 L1")]
    [TestCase("M572 D0 S0.9:0.1")]
    [TestCase("M572 D0 S0.9:0.1 L-1")]
    [TestCase("M572 D0 S0.1:0.2:0.3 L1")]
    [TestCase("M572 D0:2 S0.1")]
    [TestCase("M572 D-1 S0.1")]
    public async Task M572RefusesAMalformedCode(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, TwoExtruders);

        string reply = await bench.Host.ExecuteCodeAsync(code);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K0), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K1), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.D), Is.Null);
        });
    }

    /// <summary>
    /// L only applies to two values: with one it is ignored, and the pressure advance is single-slope
    /// </summary>
    /// <remarks>RRF Move::ConfigurePressureAdvance reads L only when S has two values</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572IgnoresLWithASingleValue()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0 S0.1 L5")).Trim(), Is.Empty);

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>().Message.Values[0].Dk,
                        Is.EqualTo(Half.PositiveInfinity));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.D), Is.Null);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K1), Is.EqualTo(0.1f));
        });
    }

    /// <summary>
    /// A first coefficient of zero turns pressure advance off whatever the second is, so the
    /// distance is reported as null and the report shows the single value, while the board is sent
    /// what the code gave
    /// </summary>
    /// <remarks>
    /// RRF ExtruderShaper::SetParameters makes dk infinite when k0 is zero, which is what the object
    /// model and AppendParameters report; the CAN message is built from the parameters as given
    /// (ShortPressureAdvanceParameters(params)) and the board's own SetParameters does the same
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572WithAZeroFirstCoefficientIsSingleSlope()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M572 D0 S0:0.1 L5")).Trim(), Is.Empty);

        CanMessageMultipleDrivesRequestShortPressureAdvanceParameters sent =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>().Message;
        Assert.Multiple(async () =>
        {
            Assert.That(sent.Values[0].K[0], Is.EqualTo((Half)0.0f));
            Assert.That(sent.Values[0].K[1], Is.EqualTo((Half)0.1f));
            Assert.That(sent.Values[0].Dk, Is.EqualTo((Half)5.0f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K0), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K1), Is.EqualTo(0.1f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.D), Is.Null);
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M572")).TrimEnd(), Is.EqualTo("Extruder pressure advance: 0.000"));
    }

    /// <summary>
    /// A board refusing the parameters makes M572 an error carrying its reason, and the object
    /// model keeps what was asked for
    /// </summary>
    /// <remarks>
    /// Duet3Expansion CommandProcessor.cpp HandlePressureAdvance answers "No such driver" as an
    /// error for a driver it does not have. RRF has already set the extruder's shaper by then, and
    /// SetRemoteDriverValues carries on to the next board
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572ReportsABoardThatRefusesIt()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, TwoExtruders);
        string refusal = $"No such driver {DriversRig.MainDriver(3)}";
        bench.CanMaster.OnCanMessage((ushort)CanMessageMultipleDrivesRequestShortPressureAdvanceParameters.MessageType,
                                     (fake, header, _) => fake.InjectStandardReply(header,
                                         header.DstAddress == DriversRig.MainDriverBoard ? CodeResult.Error : CodeResult.Ok,
                                         header.DstAddress == DriversRig.MainDriverBoard ? refusal : string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync("M572 D0:1 S0.1");

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain(refusal));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestShortPressureAdvanceParameters>().Select(m => m.Board),
                        Is.EqualTo(new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }),
                        "the refusal from the first board does not stop the second being told");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PressAdv.K0), Is.EqualTo(0.1f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].PressAdv.K0), Is.EqualTo(0.1f));
        });
    }

    /// <summary>A bare M572 on a machine with no extruders reports the heading alone</summary>
    /// <remarks>RRF Move::ConfigurePressureAdvance appends one ": value" or ", value" per extruder</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M572ReportsAMachineWithNoExtruders()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M584 E");

        Assert.That((await bench.Host.ExecuteCodeAsync("M572")).TrimEnd(), Is.EqualTo("Extruder pressure advance"));
    }
}
