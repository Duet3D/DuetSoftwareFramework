using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M350, microstepping and interpolation</summary>
[TestFixture]
[GCode("M350")]
public class M350Tests : BenchFixture
{
    /// <summary>
    /// M350 sets the microstepping, carries the interpolation flag in the message, and rescales the
    /// steps per millimetre so the axis stays calibrated
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m350-microstepping-interpolated.yaml</c>,
    /// <c>m350-microstepping-no-interpolation.yaml</c> and <c>m350-microstepping-extremes.yaml</c>.
    /// Bit 15 of the microstepping field is the interpolation flag, which is what distinguishes the I
    /// parameter reaching the driver from the firmware accepting it and dropping it
    /// </remarks>
    [TestCase(1, false)]
    [TestCase(16, true)]
    [TestCase(256, true)]
    public async Task M350SetsMicrosteppingAndInterpolation(int microstepping, bool interpolated)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync(
                        $"M350 X{microstepping} Y{microstepping} Z{microstepping} I{(interpolated ? 1 : 0)}")).Trim(),
                    Is.Empty);

        (_, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.Values[0].GetMicrostepping() & 0x03FF, Is.EqualTo(microstepping),
                        "the microstepping is in bits 0-9");
            Assert.That((sent.Values[0].GetMicrostepping() & 0x8000) != 0, Is.EqualTo(interpolated),
                        "and bit 15 asks the driver to interpolate");
            Assert.That(sent.Values[0].GetStepsPerUnit(), Is.EqualTo(80.0f * microstepping / 16).Within(1e-3),
                        "with the steps per millimetre rescaled from the microstepping in force "
                        + "(RRF GCodes.cpp ChangeMicrostepping)");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Value),
                        Is.EqualTo(microstepping));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Interpolated),
                        Is.EqualTo(interpolated));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed), Is.False,
                        "and a drive whose microstepping changed is no longer known to be where it "
                        + "says it is (RRF GCodes2.cpp case 350, SetAxisNotHomed)");
        });
    }

    /// <summary>
    /// M350 E reaches the extruder's own board, which is a different board from the axes'
    /// </summary>
    /// <remarks><c>testcases/drivers/m350-microstepping-for-extruder.yaml</c></remarks>
    [Test]
    public async Task M350SetsExtruderMicrostepping()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M350 E64 I1");

        var sent = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent, Has.Count.EqualTo(1), "only the extruder was named");
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b1 }.GetActualDataLength()));
        });

        await bench.Host.ExecuteCodeAsync("M350 E16 I0");
        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(),
                    Is.EqualTo("Microstepping - X:16(on), Y:16(on), Z:16(on), E:16"),
                    "the report suffixes (on) for an interpolating drive and nothing for one that is "
                    + "not (m350-microstepping-for-extruder.yaml)");
    }

    /// <summary>
    /// A bare M350 reports every drive, and a machine that never mentioned M350 reports x16
    /// interpolated, which is the state RepRapFirmware starts every drive in
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m350-report-microstepping.yaml</c>. Move::Init runs
    /// <c>SetDriverMicrostepping(drive, 16, true)</c> for every drive before config.g
    /// </remarks>
    [Test]
    public async Task M350ReportsTheStateEveryDriveStartsIn()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.WithoutDriveSettings);

        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(),
                    Is.EqualTo("Microstepping - X:16(on), Y:16(on), Z:16(on), E:16(on)"));
    }

    /// <summary>
    /// Leaving I out turns interpolation off, and any I above zero turns it on
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 reads <c>interp = gb.Seen('I') &amp;&amp; gb.GetIValue() &gt; 0</c> once
    /// for the whole line, so a drive named without I loses the interpolation the preamble gave it
    /// </remarks>
    [TestCase("", false)]
    [TestCase(" I2", true)]
    public async Task M350InterpolatesOnlyWhenIIsPositive(string interpolation, bool interpolated)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M350 X32{interpolation}")).Trim(), Is.Empty);

        (_, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(async () =>
        {
            Assert.That((sent.Values[0].GetMicrostepping() & 0x8000) != 0, Is.EqualTo(interpolated));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Interpolated),
                        Is.EqualTo(interpolated));
        });
    }

    /// <summary>
    /// A line naming no configured drive is the report, whatever else it carries, and changes nothing
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 only looks for the letters of axes that exist, and I is not a drive,
    /// so <c>seen</c> stays false and the report branch runs
    /// </remarks>
    [TestCase("M350 I0")]
    [TestCase("M350 A32 I1")]
    public async Task M350WithoutADriveReports(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        string report = await bench.Host.ExecuteCodeAsync("M350");

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(report), "the same report a bare M350 gives");
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Interpolated), Is.True,
                        "I alone does not touch any drive");
        });
    }

    /// <summary>
    /// The report lists every extruder after one E, and a machine without extruders ends on the bare E
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 appends "E" and then ":n" per extruder. The x1 case is
    /// <c>m350-microstepping-extremes.yaml</c>, whose reply is "Microstepping - X:1, Y:1, Z:1, E:16(on)"
    /// </remarks>
    [TestCaseSource(nameof(Reports))]
    public async Task M350ReportsEveryExtruder(string config, string expected)
    {
        await using Bench bench = await Bench.StartAsync(config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(), Is.EqualTo(expected));
    }

    private static readonly TestCaseData[] Reports =
    [
        new TestCaseData(Machines.Xyz.Lines, "Microstepping - X:16(on), Y:16(on), Z:16(on), E")
            .SetArgDisplayNames("no extruders"),
        new TestCaseData(DriversRig.Config.Lines + "\nM350 X1 Y1 Z1 I0", "Microstepping - X:1, Y:1, Z:1, E:16(on)")
            .SetArgDisplayNames("x1 without interpolation"),
        new TestCaseData(DriversRig.Config.Lines + $"\nM584 E{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)}\nM350 E16:32 I1",
                         "Microstepping - X:16(on), Y:16(on), Z:16(on), E:16(on):32(on)")
            .SetArgDisplayNames("two extruders"),
    ];

    /// <summary>
    /// One request goes to each board, carrying every driver of every drive named on it in driver
    /// order, with the steps per millimetre each drive was rescaled to
    /// </summary>
    /// <remarks>
    /// RRF Move.cpp UpdateRemoteStepsPerMmAndMicrostepping collects every driver of every drive into a
    /// CanDriversData, which keeps them sorted by board and driver, and CanInterface.cpp
    /// SetRemoteDriverValues sends one message per board. Z is on two drivers here, which both take
    /// Z's values. <c>m350-microstepping-for-extruder.yaml</c> records E64 as 1680 steps/mm with 0x8040
    /// </remarks>
    [Test]
    public async Task M350SendsOneRequestPerBoard()
    {
        await using Bench bench = await Bench.StartAsync(
            DriversRig.Config, $"M584 Z{DriversRig.MainDriver(2)}:{DriversRig.MainDriver(4)}");
        float xSteps = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm);
        float zSteps = await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm);
        float eSteps = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm);

        Assert.That((await bench.Host.ExecuteCodeAsync("M350 X32 Z64 E64 I1")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent, Has.Count.EqualTo(2), "one request per board");
        var payloads = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent[0].Message.DriversToUpdate, Is.EqualTo(0b10101), "X on driver 0 and Z on drivers 2 and 4; Y was not named");
            Assert.That(payloads[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b10101 }.GetActualDataLength()));
            Assert.That(sent[0].Message.Values[0].GetStepsPerUnit(), Is.EqualTo(xSteps * 2).Within(1e-3));
            Assert.That(sent[0].Message.Values[0].GetMicrostepping(), Is.EqualTo(0x8000 | 32));
            for (int i = 1; i <= 2; i++)
            {
                Assert.That(sent[0].Message.Values[i].GetStepsPerUnit(), Is.EqualTo(zSteps * 4).Within(1e-3));
                Assert.That(sent[0].Message.Values[i].GetMicrostepping(), Is.EqualTo(0x8000 | 64));
            }

            Assert.That(sent[1].Board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent[1].Message.DriversToUpdate, Is.EqualTo(0b1));
            Assert.That(payloads[1].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b1 }.GetActualDataLength()));
            Assert.That(sent[1].Message.Values[0].GetStepsPerUnit(), Is.EqualTo(eSteps * 4).Within(1e-3));
            Assert.That(sent[1].Message.Values[0].GetMicrostepping(), Is.EqualTo(0x8000 | 64));
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Microstepping.Value), Is.EqualTo(64));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm), Is.EqualTo(zSteps * 4).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Microstepping.Value), Is.EqualTo(64));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm), Is.EqualTo(eSteps * 4).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Microstepping.Value), Is.EqualTo(16),
                        "and Y, which was not named, keeps what it had");
        });
    }

    /// <summary>
    /// E takes one value per extruder, and a single value applies to all of them
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 reads E with <c>GetIntArray(eVals, eCount, true)</c>, which pads a
    /// single value to every extruder
    /// </remarks>
    [TestCase("E32:64", 32, 64)]
    [TestCase("E8", 8, 8)]
    public async Task M350SetsEachExtruderFromItsListEntry(string parameter, int first, int second)
    {
        await using Bench bench = await Bench.StartAsync(
            DriversRig.Config, $"M584 E{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)}\nM350 E16:16 I1");
        float[] before =
        [
            await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm),
            await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].StepsPerMm),
        ];

        Assert.That((await bench.Host.ExecuteCodeAsync($"M350 {parameter} I1")).Trim(), Is.Empty);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Microstepping.Value), Is.EqualTo(first));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].Microstepping.Value), Is.EqualTo(second));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm),
                        Is.EqualTo(before[0] * first / 16).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].StepsPerMm),
                        Is.EqualTo(before[1] * second / 16).Within(1e-3));
        });

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent.Select(message => message.Board),
                    Is.EquivalentTo(first == 16
                                        ? new[] { DriversRig.MainDriverBoard }
                                        : new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }),
                    "each changed extruder's board is told, and a skipped extruder's board is not");
        CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping onMain =
            sent.Single(message => message.Board == DriversRig.MainDriverBoard).Message;
        Assert.Multiple(() =>
        {
            Assert.That(onMain.DriversToUpdate, Is.EqualTo(1 << 3), "extruder 1 is driver 3 of the main driver board");
            Assert.That(onMain.Values[0].GetMicrostepping(), Is.EqualTo(0x8000 | second));
        });
    }


    /// <summary>A negative E entry refuses the code, and no extruder is changed or told</summary>
    /// <remarks>RRF GCodes2.cpp case 350 skips the extruder instead; see rrf-differences.md section 3.1</remarks>
    [Test]
    public async Task M350RefusesANegativeExtruderValue()
    {
        await using Bench bench = await Bench.StartAsync(
            DriversRig.Config, $"M584 E{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)}\nM350 E16:16 I1");

        Assert.That(await bench.Host.ExecuteCodeAsync("M350 E-1:64 I1"), Does.StartWith("Error:"));
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Microstepping.Value), Is.EqualTo(16));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].Microstepping.Value), Is.EqualTo(16));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(), Is.Empty);
        });
    }

    /// <summary>
    /// A value that cannot be read makes the line an error and tells no board, but the drives read
    /// before it keep their new microstepping
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 stores each axis as it reads it and sends the one remote update after
    /// the line, so a throw part-way leaves X changed. Y-1 fails GetUIValue, an E list longer than
    /// there are extruders fails GetIntArray's CheckArrayLength ("array too long"), and with no
    /// extruders any E value is too long
    /// </remarks>
    [TestCaseSource(nameof(RefusedLines))]
    public async Task M350KeepsWhatItReadWhenAValueIsRefused(string config, string code)
    {
        await using Bench bench = await Bench.StartAsync(config);
        float xSteps = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Value), Is.EqualTo(32),
                        "X was read before the refusal, so it stands");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(xSteps * 2).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Microstepping.Value), Is.EqualTo(16));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(), Is.Empty,
                        "the boards are told only once the whole line has been read");
        });
    }

    private static readonly TestCaseData[] RefusedLines =
    [
        new TestCaseData(DriversRig.Config.Lines, "M350 X32 Y-1").SetArgDisplayNames("M350 X32 Y-1"),
        new TestCaseData(DriversRig.Config.Lines, "M350 X32 E16:32").SetArgDisplayNames("M350 X32 E16:32"),
        new TestCaseData(Machines.Xyz.Lines, "M350 X32 E16").SetArgDisplayNames("M350 X32 E16 without extruders"),
    ];

    /// <summary>
    /// Only the axes M350 names lose their homed state; an extruder change un-homes nothing
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 350 calls SetAxisNotHomed for each axis whose microstepping it changed</remarks>
    [Test]
    public async Task M350UnhomesOnlyTheAxesItNames()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, new ConfigPart("G92 X0 Y0 Z0", ConfigOrder.Last));

        await bench.Host.ExecuteCodeAsync("M350 E32 I1");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Homed).ToArray()),
                    Is.All.True, "the extruder is not an axis");

        await bench.Host.ExecuteCodeAsync("M350 Y32 I1");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Homed).ToArray()),
                    Is.EqualTo(new[] { true, false, true }));
    }

    /// <summary>
    /// A move queued before M350 runs at the old microstepping, and the axis keeps its position in
    /// millimetres, so the next move travels only the distance asked for at the new one
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 waits for standstill (LockAllMovementSystemsAndWaitForStandstill)
    /// before changing anything, and GCodes.cpp ChangeMicrostepping calls AdjustEndpoint with the
    /// steps/mm ratio, so the motor endpoint is rescaled with the steps per millimetre
    /// </remarks>
    [Test]
    public async Task M350KeepsThePositionAcrossTheChange()
    {
        await using Bench bench = await Bench.StartAsync(Machines.X);

        await bench.Host.ExecuteCodeAsync("G1 X10 F6000");
        Assert.That((await bench.Host.ExecuteCodeAsync("M350 X32 I1")).Trim(), Is.Empty);
        int before = bench.CanMaster.ScheduledSteps(Machines.XDriver);
        IReadOnlyList<ScheduledMove> after = await bench.RunMoveAsync("G1 X20 F6000");

        Assert.Multiple(async () =>
        {
            Assert.That(before, Is.EqualTo((int)(10 * Machines.XyStepsPerMm)),
                        "the queued move went out whole, at x16, before the microstepping changed");
            Assert.That(after.Steps(Machines.XDriver), Is.EqualTo((int)(10 * Machines.XyStepsPerMm * 2)),
                        "and the next 10 mm is 10 mm at x32");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(20.0).Within(1e-3));
        });
    }

    /// <summary>
    /// A board refusing the microstepping makes M350 an error carrying its reason, the other boards
    /// are still told, and the object model keeps what was asked for
    /// </summary>
    /// <remarks>
    /// With every driver remote, RRF's Move::SetMicrostepping has no local driver to check, so it
    /// stores the value and rescales the steps per millimetre before sending; the board's
    /// CommandProcessor.cpp SetStepsPerMmAndMicrostepping refuses anything that is not a power of two
    /// from 1 to 256 ("Driver 1.0 does not support x3 microstepping"). SetRemoteDriverValues carries
    /// on to the next board after an error
    /// </remarks>
    [TestCase(3)]
    [TestCase(512)]
    public async Task M350ReportsABoardThatRefusesTheMicrostepping(int microstepping)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        float xSteps = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm);
        float eSteps = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm);
        string refusal = $"Driver {DriversRig.MainDriver(0)} does not support x{microstepping} microstepping";
        bench.CanMaster.OnCanMessage((ushort)CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping.MessageType,
                                     (fake, header, _) => fake.InjectStandardReply(header,
                                         header.DstAddress == DriversRig.MainDriverBoard ? CodeResult.Error : CodeResult.Ok,
                                         header.DstAddress == DriversRig.MainDriverBoard ? refusal : string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync($"M350 X{microstepping} E{microstepping}");

        float Expected(float steps) => MathF.Max(steps * microstepping / 16, Move.MinimumStepsPerMm);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(reply, Does.Contain(refusal), "the board's reason is carried back");
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Select(m => m.Board),
                        Is.EqualTo(new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }),
                        "the refusal from the first board does not stop the second being told");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Value), Is.EqualTo(microstepping));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(Expected(xSteps)).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Microstepping.Value), Is.EqualTo(microstepping));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm), Is.EqualTo(Expected(eSteps)).Within(1e-3));
        });
    }

    /// <summary>
    /// A board that never answers makes M350 an error, and the object model keeps what was asked for
    /// </summary>
    /// <remarks>
    /// CanInterface.cpp SetRemoteDriverValues returns canResponseTimeout from
    /// SendRequestAndGetStandardReply, after the host has already stored the microstepping
    /// </remarks>
    [Test]
    public async Task M350ReportsABoardThatNeverAnswers()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        bench.CanMaster.NeverAnswer<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();

        string reply = await bench.Host.ExecuteCodeAsync("M350 X32 I1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Value), Is.EqualTo(32));
        });
    }
}
