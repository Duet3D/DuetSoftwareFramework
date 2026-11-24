using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M915, stall detection</summary>
[TestFixture]
[GCode("M915")]
public class M915Tests : BenchFixture
{
    /// <summary>
    /// M915 P addresses a driver directly, and the settings are recorded under
    /// boards[].drivers[].config.stallDetection
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection.yaml</c>. RepRapFirmware forwards the settings and
    /// keeps nothing; the object model entry is rrf-differences.md section 3, which added
    /// <c>stallDetection</c> because stall configuration had no home at all
    /// </remarks>
    [Test]
    public async Task M915PConfiguresOneDriver()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        // T, the coolStep register, is the one parameter the references leave alone: the datasheet
        // warns that a wrong value silently reduces motor current, so no case sends it to the rig
        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{Expansion.Driver(board, 0)} S5 F1 H300 T100 R{StallAction.Log:D}")).Trim(), Is.Empty);

        (byte sentTo, CanMessageM915 sent) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(sent.d, Is.EqualTo(1), "the bitmap is the board's own driver numbers, which "
                                               + "only this side can work out");
            Assert.That(sent.S, Is.EqualTo(5), "S is the StallGuard threshold");
            Assert.That(sent.F, Is.EqualTo(1), "F averages over four full steps");
            Assert.That(sent.H, Is.EqualTo(300), "H is the full steps per second below which "
                                                 + "StallGuard is not trusted");
            Assert.That(sent.T, Is.EqualTo(100), "T is the coolStep register value");
            Assert.That((StallAction?)sent.R, Is.EqualTo(StallAction.Log), "R is the action on stall");
        });

        DriverStallDetection stall = await bench.Host.ReadModelAsync(
            model => model.Boards[board].Drivers![0].Config.StallDetection);
        Assert.Multiple(() =>
        {
            Assert.That(stall.Threshold, Is.EqualTo(5));
            Assert.That(stall.Filter, Is.True);
            Assert.That(stall.MinimumSpeed, Is.EqualTo(300));
            Assert.That(stall.CoolStep, Is.EqualTo(100));
            Assert.That(stall.RaiseEvent, Is.True);
        });
    }

    /// <summary>
    /// M915 by axis letter expands each letter to every driver of that axis, and an extruder number
    /// to the extruder's driver, so a machine whose drives sit on two boards gets one request each
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection-by-axis-letter.yaml</c>: X, Y and Z are on one board
    /// and the extruder on another, so the settings go out as two requests with different bitmaps
    /// </remarks>
    [Test]
    public async Task M915ByAxisLetterAndExtruderNumber()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M915 X Y Z S5 F1 H150 R0")).Trim(), Is.Empty);
        Assert.That((await bench.Host.ExecuteCodeAsync("M915 E0 S4 F0 H200 R0")).Trim(), Is.Empty,
                    "an extruder is named by number, which is a drive as much as a letter is");

        var sent = bench.CanMaster.CanMessages<CanMessageM915>();
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent[0].Message.d, Is.EqualTo(0b111), "X, Y and Z are drivers 0, 1 and 2 there");
            Assert.That(sent[0].Message.S, Is.EqualTo(5));
            Assert.That(sent[1].Board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent[1].Message.d, Is.EqualTo(1), "and the extruder is driver 0 of its own board");
            Assert.That(sent[1].Message.S, Is.EqualTo(4));
        });
    }

    /// <summary>
    /// A bare E is refused rather than standing for every extruder, quoting where it stood
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-missing-parameters-drivers.yaml</c>: Move::ConfigureStallDetection
    /// reads the extruder list with <c>GetUnsignedArray(..., false)</c>, which refuses to supply a
    /// default, so the letter alone is a parse error
    /// </remarks>
    [Test]
    public async Task M915RefusesABareExtruderLetter()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M915 E");
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: at column 7: M915: expected number after 'E'"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Is.Empty,
                        "and nothing is sent, so no driver is configured from a default nobody named");
        });
    }

    /// <summary>
    /// Naming drives with no values is a report, which the boards answer and this side concatenates
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-report-stall-settings.yaml</c> and
    /// <c>m915-stall-detection-by-axis-letter.yaml</c>. The derived mm/sec figures in it are the
    /// board's, worked out from the steps per millimetre and microstepping it was last sent, so the
    /// report travels back unchanged
    /// </remarks>
    [Test]
    public async Task M915ReportsFromTheBoards()
    {
        const string boardReport = "Driver 1.0: stall threshold 4, filter on, full steps/sec 250 (50.0 mm/sec), "
                                   + "coolstep threshold 197 (50.0 mm/sec), event on stall: yes";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM915>(
                message => message.S is null ? boardReport : string.Empty)).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M915 P{Expansion.Driver(DriversRig.MainDriverBoard, 0)}");
        Assert.That(reply.TrimEnd(), Is.EqualTo(boardReport));
    }

    /// <summary>
    /// P takes a list of drivers, which may be on several boards, and each board is sent the bitmap of
    /// its own drivers in the list
    /// </summary>
    /// <remarks>
    /// Move::ConfigureStallDetection reads P with <c>GetDriverIdArray</c> and adds each remote driver to
    /// a CanDriversList, which CanInterface::GetSetRemoteDriverStallParameters walks board by board. The
    /// wiki documents <c>Pnnn:nnn:...</c> and gives <c>M915 P0:2:3 S10 F1 R0</c> as its example. No
    /// regression case sends a list
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M915PTakesADriverListAcrossBoards()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        int[] before = await ThresholdsAsync(bench, DriversRig.MainDriverBoard);

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M915 P{DriversRig.MainDriver(0)}:{DriversRig.MainDriver(2)}:{DriversRig.ClosedLoopDriver(0)} S5");

        var sent = bench.CanMaster.CanMessages<CanMessageM915>();
        int[] expected = [.. before];
        expected[0] = expected[2] = 5;
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(sent.Select(message => message.Board),
                        Is.EqualTo(new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }));
            Assert.That(sent.Select(message => message.Message.d), Is.EqualTo(new ushort?[] { 0b101, 0b1 }),
                        "each board gets the bitmap of its own drivers in the list");
            Assert.That(sent.Select(message => message.Message.S), Is.All.EqualTo(5));
            Assert.That(await ThresholdsAsync(bench, DriversRig.MainDriverBoard), Is.EqualTo(expected),
                        "the two listed drivers of the first board are recorded and the one between them is not");
            Assert.That(await ThresholdsAsync(bench, DriversRig.ClosedLoopBoard), Is.EqualTo(new[] { 5 }));
        });
    }

    /// <summary>
    /// P, axis letters and extruder numbers may be given together, and a driver named twice is still
    /// one driver
    /// </summary>
    /// <remarks>
    /// Move::ConfigureStallDetection collects P, then every axis letter seen, then E into one
    /// CanDriversList, whose AddEntry keeps a single entry per driver
    /// </remarks>
    [Test]
    public async Task M915CombinesPWithAxisLettersAndExtruders()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{DriversRig.MainDriver(3)} X E0 S5")).Trim(), Is.Empty);
        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{DriversRig.MainDriver(0)} X S6")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageM915>();
        Assert.That(sent, Has.Count.EqualTo(3));
        Assert.Multiple(async () =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent[0].Message.d, Is.EqualTo(0b1001), "the driver named by P and X's driver go to their board together");
            Assert.That(sent[0].Message.S, Is.EqualTo(5));
            Assert.That(sent[1].Board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent[1].Message.d, Is.EqualTo(1), "and the extruder's driver to its own");
            Assert.That(sent[1].Message.S, Is.EqualTo(5));
            Assert.That(sent[2].Board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent[2].Message.d, Is.EqualTo(1), "X's driver named by P as well is sent once");
            Assert.That(sent[2].Message.S, Is.EqualTo(6));

            int[] thresholds = await ThresholdsAsync(bench, DriversRig.MainDriverBoard);
            Assert.That(thresholds[0], Is.EqualTo(6));
            Assert.That(thresholds[3], Is.EqualTo(5));
            Assert.That(await ThresholdsAsync(bench, DriversRig.ClosedLoopBoard), Is.EqualTo(new[] { 5 }));
        });
    }

    /// <summary>
    /// An axis letter stands for every motor of the axis, so an axis driven by two motors configures both
    /// </summary>
    /// <remarks>
    /// Move::ConfigureStallDetection hands each axis seen to IterateDrivers, which visits every driver of
    /// the axis. <c>testcases/drivers/m915-stall-detection-by-axis-letter.yaml</c> describes the failure
    /// this guards as "sensorless homing is configured on some of an axis's motors and not others"
    /// </remarks>
    [Test]
    public async Task M915ByAnAxisWithSeveralMotors()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config,
                                                         $"M584 X{DriversRig.MainDriver(0)}:{DriversRig.MainDriver(3)}");

        Assert.That((await bench.Host.ExecuteCodeAsync("M915 X S5 F1 H150")).Trim(), Is.Empty);

        (byte board, CanMessageM915 sent) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Has.Count.EqualTo(1),
                        "both motors are on one board, so one request carries them");
            Assert.That(board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent.d, Is.EqualTo(0b1001));
            foreach (int driver in new[] { 0, 3 })
            {
                DriverStallDetection stall = await bench.Host.ReadModelAsync(
                    model => model.Boards[DriversRig.MainDriverBoard].Drivers![driver].Config.StallDetection);
                Assert.That(stall.Threshold, Is.EqualTo(5), $"driver {driver}");
                Assert.That(stall.Filter, Is.True, $"driver {driver}");
                Assert.That(stall.MinimumSpeed, Is.EqualTo(150), $"driver {driver}");
            }
        });
    }

    private static readonly TestCaseData[] ExtraAxes =
    [
        new TestCaseData($"M584 U{DriversRig.MainDriver(3)} V{DriversRig.MainDriver(4)} W{DriversRig.MainDriver(5)}", "U V W")
            .SetArgDisplayNames("U V W"),
        new TestCaseData($"M584 A{DriversRig.MainDriver(3)} B{DriversRig.MainDriver(4)} C{DriversRig.MainDriver(5)}", "A B C")
            .SetArgDisplayNames("A B C"),
    ];

    /// <summary>
    /// Axes past Z, linear and rotary, are found by their letters like X, Y and Z, both to configure
    /// and to report
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection-extra-axes.yaml</c> and
    /// <c>m915-stall-detection-rotary-axes.yaml</c>: Move::ConfigureStallDetection walks every axis
    /// letter up to GetTotalAxes. The rotary case maps A, B and C to main board drivers 0.3 to 0.5,
    /// which do not exist here, so the scenario puts them on the expansion board the way the extra-axes
    /// case does
    /// </remarks>
    [Test]
    [TestCaseSource(nameof(ExtraAxes))]
    public async Task M915ByExtraAxisLetters(string mapping, string letters)
    {
        await using Bench bench = await Bench.With(DriversRig.Config, mapping)
                                             .WithController(AnswerAsBoards)
                                             .StartAsync();
        int xThreshold = (await ThresholdsAsync(bench, DriversRig.MainDriverBoard))[0];

        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 {letters} S5 F1 H150 R{StallAction.None:D}")).Trim(), Is.Empty);
        (byte board, CanMessageM915 set) = bench.CanMaster.LastCanMessage<CanMessageM915>();

        string report = await bench.Host.ExecuteCodeAsync($"M915 {letters}");
        (_, CanMessageM915 asked) = bench.CanMaster.LastCanMessage<CanMessageM915>();

        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(set.d, Is.EqualTo(0b111000), "the three axes are drivers 3, 4 and 5 of the board");
            Assert.That((set.S, set.F, set.H, (StallAction?)set.R), Is.EqualTo(((sbyte?)5, (byte?)1, (ushort?)150, (StallAction?)StallAction.None)));
            foreach (int driver in new[] { 3, 4, 5 })
            {
                DriverStallDetection stall = await bench.Host.ReadModelAsync(
                    model => model.Boards[DriversRig.MainDriverBoard].Drivers![driver].Config.StallDetection);
                Assert.That((stall.Threshold, stall.Filter, stall.MinimumSpeed, stall.RaiseEvent),
                            Is.EqualTo((5, true, 150, false)), $"driver {driver}");
            }
            Assert.That((await ThresholdsAsync(bench, DriversRig.MainDriverBoard))[0], Is.EqualTo(xThreshold),
                        "and X, which was not named, is left alone");

            Assert.That(asked.d, Is.EqualTo(0b111000));
            Assert.That(IsReport(asked), Is.True, "the report request carries the bitmap and nothing else");
            Assert.That(report.TrimEnd(), Is.EqualTo(BoardReport(DriversRig.MainDriverBoard, 0b111000)));
        });
    }

    /// <summary>
    /// A parameter left out is not sent, so the board keeps what it had, and the object model keeps it too
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection.yaml</c>: after <c>S3 F0 H200 R0</c> its reference
    /// sends <c>S5 F1</c> with paramMap 0x7 (d, S and F) and <c>R1 H300</c> with paramMap 0x29 (d, H
    /// and R). CanMessageGenericConstructor::PopulateFromCommand adds only the letters seen, and the wiki
    /// says "If any of the S, F, T and R parameters are absent, the previous values ... will continue to
    /// be used"
    /// </remarks>
    [Test]
    public async Task M915SendsOnlyTheParametersGiven()
    {
        string driver = DriversRig.MainDriver(0);
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, $"M915 P{driver} S3 F0 H200 T100 R{StallAction.None:D}");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{driver} S5 F1")).Trim(), Is.Empty);
        (_, CanMessageM915 first) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        DriverStallDetection afterFirst = await StallAsync(bench, DriversRig.MainDriverBoard, 0);
        (int, bool, int, int, bool) firstModel = (afterFirst.Threshold, afterFirst.Filter, afterFirst.MinimumSpeed,
                                                  afterFirst.CoolStep, afterFirst.RaiseEvent);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{driver} R{StallAction.Log:D} H300")).Trim(), Is.Empty);
        (_, CanMessageM915 second) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        DriverStallDetection afterSecond = await StallAsync(bench, DriversRig.MainDriverBoard, 0);

        Assert.Multiple(() =>
        {
            Assert.That((first.d, first.S, first.F), Is.EqualTo(((ushort?)1, (sbyte?)5, (byte?)1)));
            Assert.That((first.H, first.T, first.R), Is.EqualTo(((ushort?)null, (ushort?)null, (byte?)null)),
                        "H, T and R were not given, so they are not sent");
            Assert.That(firstModel, Is.EqualTo((5, true, 200, 100, false)),
                        "and the object model keeps the H, T and R it had");

            Assert.That((second.d, second.H, (StallAction?)second.R), Is.EqualTo(((ushort?)1, (ushort?)300, (StallAction?)StallAction.Log)));
            Assert.That((second.S, second.F, second.T), Is.EqualTo(((sbyte?)null, (byte?)null, (ushort?)null)));
            Assert.That((afterSecond.Threshold, afterSecond.Filter, afterSecond.MinimumSpeed, afterSecond.CoolStep, afterSecond.RaiseEvent),
                        Is.EqualTo((5, true, 300, 100, true)));
        });
    }

    private static readonly TestCaseData[] Limits =
    [
        new TestCaseData(-128, 65535, StallAction.RaiseEventAlias).SetArgDisplayNames("lowest S, highest T and R"),
        new TestCaseData(127, 0, StallAction.RaiseEvent).SetArgDisplayNames("highest S, lowest T"),
        new TestCaseData(-10, 1, StallAction.Log).SetArgDisplayNames("negative S"),
    ];

    /// <summary>
    /// The extremes of S, T and R are accepted and sent as given
    /// </summary>
    /// <remarks>
    /// Move::ConfigureStallDetection on a Duet 3 reads S with <c>TryGetLimitedIValue('S', ..., -128,
    /// 127)</c> ("TMC2209 accepts 0..255 which we map to -128..127. TMC2160 accepts -64..63. We may have
    /// a mixture in a CAN bus system"), T with <c>TryGetLimitedUIValue('T', ..., 1u &lt;&lt; 16)</c> and R
    /// with <c>TryGetLimitedUIValue('R', ..., 4)</c>. The wiki gives -64..63 for most drivers and
    /// -128..127 for the TMC2209; the board clamps to its own driver's range
    /// </remarks>
    [Test]
    [TestCaseSource(nameof(Limits))]
    public async Task M915AcceptsTheLimitsOfEachParameter(int threshold, int coolStep, StallAction action)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{DriversRig.MainDriver(0)} S{threshold} T{coolStep} R{action:D}")).Trim(), Is.Empty);

        (_, CanMessageM915 sent) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        DriverStallDetection stall = await StallAsync(bench, DriversRig.MainDriverBoard, 0);
        Assert.Multiple(() =>
        {
            Assert.That((sent.S, sent.T, (StallAction?)sent.R), Is.EqualTo(((sbyte?)threshold, (ushort?)coolStep, (StallAction?)action)));
            Assert.That((stall.Threshold, stall.CoolStep, stall.RaiseEvent), Is.EqualTo((threshold, coolStep, true)));
        });
    }

    /// <summary>
    /// A value outside the range a parameter takes is refused before anything is sent, so neither the
    /// board nor the object model changes
    /// </summary>
    /// <remarks>
    /// Move::ConfigureStallDetection reads S, T and R through GetLimitedIValue and GetLimitedUIValue,
    /// which throw "parameter 'S' too low" (or "too high") before CanInterface::GetSetRemoteDriverStallParameters
    /// is reached. The limits are those of <see cref="M915AcceptsTheLimitsOfEachParameter"/>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    [TestCase("S-129")]
    [TestCase("S128")]
    [TestCase("T65536")]
    public Task M915RefusesAValueOutOfRange(string value) => AssertRefusedAsync(value);

    /// <summary>An R that is no action is refused before anything is sent, like <see cref="M915RefusesAValueOutOfRange"/></summary>
    [Test]
    [TestCase("R4")]
    [TestCase("R-1")]
    [TestCase("S5 F1 R4")]
    public Task M915RefusesAnActionThatDoesNotExist(string values) => AssertRefusedAsync(values, 'R');

    /// <summary>Assert that M915 with the given values is refused naming a parameter, the first one by default, and changes nothing</summary>
    private static async Task AssertRefusedAsync(string values, char? parameter = null)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        DriverStallDetection before = await StallAsync(bench, DriversRig.MainDriverBoard, 0);
        (int, bool, int, bool) expected = (before.Threshold, before.Filter, before.CoolStep, before.RaiseEvent);

        string reply = await bench.Host.ExecuteCodeAsync($"M915 P{DriversRig.MainDriver(0)} {values}");

        DriverStallDetection after = await StallAsync(bench, DriversRig.MainDriverBoard, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(reply, Does.Contain($"'{parameter ?? values[0]}'"), "naming the parameter");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Is.Empty, "nothing reaches the board");
            Assert.That((after.Threshold, after.Filter, after.CoolStep, after.RaiseEvent), Is.EqualTo(expected));
        });
    }

    /// <summary>
    /// R is sent as given, and any action other than none is recorded as the board raising an event
    /// </summary>
    /// <remarks>
    /// The main board passes R through PopulateFromCommand unchanged. Duet3Expansion's ProcessM915 calls
    /// <c>SetOrResetEventOnStall(drivers, rParam != 0)</c>, so on an expansion board R1 raises an event
    /// like R2 and R3, and its report says "event on stall: yes" (the reference of
    /// <c>testcases/drivers/m915-report-stall-settings.yaml</c>, set up with R1). Only RRF's local drivers
    /// keep R1 apart, as log-only. The wiki says R1 "just log it" without that distinction
    /// </remarks>
    [Test]
    [TestCase(StallAction.None)]
    [TestCase(StallAction.Log)]
    [TestCase(StallAction.RaiseEvent)]
    [TestCase(StallAction.RaiseEventAlias)]
    public async Task M915RecordsTheActionOnStall(StallAction action)
    {
        string driver = DriversRig.MainDriver(0);
        StallAction other = action == StallAction.None ? StallAction.Log : StallAction.None;
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, $"M915 P{driver} R{other:D}");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{driver} R{action:D}")).Trim(), Is.Empty);

        (_, CanMessageM915 sent) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        Assert.Multiple(async () =>
        {
            Assert.That((StallAction?)sent.R, Is.EqualTo(action), "R goes to the board as it was written");
            Assert.That((await StallAsync(bench, DriversRig.MainDriverBoard, 0)).RaiseEvent, Is.EqualTo(action != StallAction.None));
        });
    }

    /// <summary>
    /// Naming drives on several boards with no values reports from each board, concatenated
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection-by-axis-letter.yaml</c>, its last line <c>M915 X Y Z
    /// E0</c>: one request per board carrying only the bitmap, and CanInterface::GetSetRemoteDriverStallParameters
    /// appends each board's reply to the output buffer on a line of its own
    /// </remarks>
    [Test]
    public async Task M915ReportOnSeveralBoardsConcatenates()
    {
        await using Bench bench = await Bench.With(DriversRig.Config).WithController(AnswerAsBoards).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M915 X Y Z E0");

        var sent = bench.CanMaster.CanMessages<CanMessageM915>();
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(sent.Select(message => (message.Board, message.Message.d)),
                        Is.EqualTo(new (byte, ushort?)[] { (DriversRig.MainDriverBoard, 0b111), (DriversRig.ClosedLoopBoard, 1) }));
            Assert.That(sent.Select(message => IsReport(message.Message)), Is.All.True);
            Assert.That(reply.TrimEnd(), Is.EqualTo(BoardReport(DriversRig.MainDriverBoard, 0b111) + "\n"
                                                    + BoardReport(DriversRig.ClosedLoopBoard, 1)));
        });
    }

    /// <summary>
    /// A code that names no driver is not an error: with values it changes nothing, and without them it
    /// reports nothing, because the main board has no drivers of its own
    /// </summary>
    /// <remarks>
    /// Move::ConfigureStallDetection ignores a letter that is not an axis, and with values seen it calls
    /// GetSetRemoteDriverStallParameters, which returns ok for an empty list. With none seen it reports
    /// the main board's own drivers, of which there are none here; a build without local stall
    /// detection replies "No local drivers have stall detection", successfully.
    /// <c>testcases/drivers/m915-stall-detection-rotary-axes.yaml</c> fails on DSF with "No drivers
    /// specified" because its A, B and C are on main board drivers, which DSF does not have
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    [TestCase("M915")]
    [TestCase("M915 S5 F1")]
    [TestCase("M915 A S5")]
    public async Task M915NamingNoDriverIsNotAnError(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:").And.Not.StartWith("Warning:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Is.Empty);
        });
    }

    /// <summary>
    /// A driver on board 0 is refused, because DuetCANMaster has no drivers
    /// </summary>
    /// <remarks>
    /// rrf-differences.md section 1 lists local stall detection among what is not ported, since board
    /// 0 has no drivers. RepRapFirmware configures its own drivers there, refusing only a number
    /// past its last smart driver with "Invalid local drive number"
    /// </remarks>
    [Test]
    public async Task M915RefusesADriverOnBoard0()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M915 P{Expansion.Driver(0, 1)} S5");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[0].Drivers?.Count ?? 0), Is.Zero,
                        "and board 0 does not gain a driver");
        });
    }

    /// <summary>
    /// An extruder number past the configured extruders is refused before anything is sent
    /// </summary>
    /// <remarks>
    /// RepRapFirmware skips a number at or past MaxExtruders and resolves any other to that extruder's
    /// driver, which defaults to a main board driver. DSF refuses an array element out of range, which
    /// is a deliberate parsing difference (rrf-differences.md, Error handling)
    /// </remarks>
    [Test]
    public async Task M915RefusesAnExtruderThatDoesNotExist()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M915 E1 S5");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Is.Empty);
        });
    }

    /// <summary>
    /// A driver number past the end of a board is sent, and the board ignores it without complaint; the
    /// board's drivers in the object model stay the ones it has
    /// </summary>
    /// <remarks>
    /// Neither side checks the number: Move::ConfigureStallDetection checks only local drivers, and
    /// Duet3Expansion's SmartDrivers::SetStallThreshold and its siblings skip a driver at or past
    /// numTmcDrivers, so ProcessM915 replies ok. boards[].drivers is the board's own list
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    [Category("NonCritical")]
    public async Task M915OnADriverPastTheEndOfABoardIsIgnoredByTheBoard()
    {
        byte drivers = DriversRig.BoardDrivers[DriversRig.MainDriverBoard];
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M915 P{DriversRig.MainDriver(drivers)} S5");

        (byte board, CanMessageM915 sent) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent.d, Is.EqualTo(1 << drivers));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[DriversRig.MainDriverBoard].Drivers!.Count),
                        Is.EqualTo(drivers), "the board does not gain a driver it has not got");
        });
    }

    /// <summary>
    /// A board that refuses is reported as an error, and the other boards are still told
    /// </summary>
    /// <remarks>
    /// The refusal is Duet3Expansion's ProcessM915 on a board without stall detection. RepRapFirmware's
    /// CanInterface::GetSetRemoteDriverStallParameters returns at the first board that does not reply
    /// ok, leaving the later boards untold. That is the outlier: SetRemoteDriverValues (M92, M350,
    /// M906, M913, M917) and SetRemoteDriverStates tell every board and report the worst result, which
    /// is what this pins (rrf-differences.md, Error handling)
    /// </remarks>
    [Test]
    [Category("NonCritical")]
    public async Task M915ReportsABoardsRefusalAndStillTellsTheOthers()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        RefuseOn(bench.CanMaster, DriversRig.MainDriverBoard);

        string reply = await bench.Host.ExecuteCodeAsync("M915 X E0 S5");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(reply, Does.Contain(StallDetectionNotSupported), "with the board's own words");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>().Select(message => message.Board),
                        Is.EqualTo(new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }));
        });
    }

    /// <summary>
    /// What a board refused is not recorded, and what the other boards took is
    /// </summary>
    /// <remarks>
    /// RepRapFirmware keeps no copy, so the board is the only record and it refused. The object model
    /// has to agree with the boards after a code that partly failed (CLAUDE.md, Implementing features)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M915DoesNotRecordWhatABoardRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M915 X E0 S3");
        RefuseOn(bench.CanMaster, DriversRig.MainDriverBoard);

        await bench.Host.ExecuteCodeAsync("M915 X E0 S5");

        Assert.Multiple(async () =>
        {
            Assert.That((await StallAsync(bench, DriversRig.MainDriverBoard, 0)).Threshold, Is.EqualTo(3),
                        "X's board refused, so X keeps the threshold it has");
            Assert.That((await StallAsync(bench, DriversRig.ClosedLoopBoard, 0)).Threshold, Is.EqualTo(5),
                        "and the extruder's board took the new one");
        });
    }

    /// <summary>
    /// A board that never answers, as one that is not on the bus does not, makes M915 an error
    /// </summary>
    /// <remarks>
    /// CanMessageGenericConstructor::SendAndGetResponse returns canResponseTimeout with RepRapFirmware's
    /// "CAN response timeout" text, and GetSetRemoteDriverStallParameters returns it
    /// </remarks>
    [Test]
    public async Task M915ReportsABoardThatNeverAnswers()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        bench.CanMaster.NeverAnswer<CanMessageM915>();

        string reply = await bench.Host.ExecuteCodeAsync("M915 X S5");
        Assert.That(reply.TrimEnd(), Does.StartWith(
            $"Error: M915: CAN response timeout: board {DriversRig.MainDriverBoard}, req type {(int)CanMessageType.M915}"));
    }

    /// <summary>
    /// A stall a board reports runs driver-stall.g, which is told the board and the driver
    /// </summary>
    /// <remarks>
    /// The board raises <c>driver_stall</c> for a driver M915 R set to anything but 0 (Duet3Expansion
    /// Move.cpp, eventOnStallDrivers). GCodes::ProcessEvent runs the macro Event::GetMacroFileName names
    /// with param.B and param.D, as the wiki's M915 notes describe
    /// </remarks>
    [Test]
    public async Task ADriverStallRunsDriverStallG()
    {
        await using Bench bench = await Bench.With(DriversRig.Config, "global stalls = 0\nglobal stallBoard = -1\nglobal stallDriver = -1")
                                             .WithSd(sd => sd.WriteSys("driver-stall.g", """
                                                 set global.stallBoard = param.B
                                                 set global.stallDriver = param.D
                                                 set global.stalls = global.stalls + 1

                                                 """))
                                             .StartAsync();

        InjectDriverStall(bench, DriversRig.MainDriverBoard, 2);

        await bench.Host.WaitForGlobalAsync("stalls", 1);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stallBoard"), Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(await bench.Host.GlobalAsync("stallDriver"), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// Without driver-stall.g a stall is reported as a warning and the machine carries on
    /// </summary>
    /// <remarks>
    /// Event::GetTextDescription words it "Driver %u.%u stall" as a WarningMessage, and
    /// Event::GetDefaultPauseReason has no case for driver_stall, so nothing pauses. The wiki: "If file
    /// driver-stall.g is not found then the default action is to report it to the console and carry on"
    /// </remarks>
    [Test]
    public async Task ADriverStallWithoutAMacroIsReportedAsAWarning()
    {
        await using Bench bench = await Bench.With(DriversRig.Config, "global jobStarted = 0\nglobal release = 0\nglobal jobDone = 0")
                                             .WithSd(sd => sd.WriteGCode("job.gcode", """
                                                 set global.jobStarted = 1
                                                 while global.release == 0
                                                   G4 P10
                                                 set global.jobDone = 1
                                                 """))
                                             .StartAsync();
        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("jobStarted", 1);

        InjectDriverStall(bench, DriversRig.MainDriverBoard, 2);

        string message = await bench.Host.WaitForMessageAsync(MessageType.Warning, $"Driver {DriversRig.MainDriver(2)} stall");
        MachineStatus status = await bench.Host.ReadModelAsync(model => model.State.Status);
        await bench.Host.ExecuteCodeAsync("set global.release = 1");
        await bench.Host.WaitForGlobalAsync("jobDone", 1);
        Assert.Multiple(() =>
        {
            Assert.That(message, Is.EqualTo($"Driver {DriversRig.MainDriver(2)} stall"));
            Assert.That(status, Is.Not.AnyOf(MachineStatus.Pausing, MachineStatus.Paused),
                        "the job was not paused, and ran on to its end once released");
        });
    }

    /// <summary>What a board without stall detection answers M915 with (Duet3Expansion CommandProcessor.cpp ProcessM915)</summary>
    private const string StallDetectionNotSupported = "stall detection not supported by this board";

    /// <summary>Whether a request asks for a report: it carries the bitmap and no value</summary>
    private static bool IsReport(CanMessageM915 message)
        => message.S is null && message.F is null && message.H is null && message.T is null && message.R is null;

    /// <summary>
    /// What a board reports for the drivers in a bitmap, a line each, as m915-stall-detection-extra-axes
    /// records them
    /// </summary>
    private static string BoardReport(byte board, ushort drivers)
        => string.Join('\n', Enumerable.Range(0, 16)
                                       .Where(driver => (drivers & (1 << driver)) != 0)
                                       .Select(driver => $"Driver {Expansion.Driver(board, driver)}: stall threshold 5, filter on, "
                                                         + "full steps/sec 150 (30.0 mm/sec), coolstep threshold 328 (30.0 mm/sec), "
                                                         + "event on stall: no"));

    /// <summary>Answer every M915 as the board it is sent to: with a report when it asks for one, else empty</summary>
    private static void AnswerAsBoards(ScriptedCanMaster canMaster)
        => canMaster.OnCanMessage((ushort)CanMessageType.M915, (fake, header, payload) =>
        {
            CanMessageM915 message = CanMessageSerializer.Deserialize<CanMessageM915>(payload);
            fake.InjectStandardReply(header, CodeResult.Ok,
                                     IsReport(message) ? BoardReport(header.DstAddress, message.d ?? 0) : string.Empty);
        });

    /// <summary>Have one board refuse every M915 as a board without stall detection does, and the others take it</summary>
    private static void RefuseOn(ScriptedCanMaster canMaster, byte refusingBoard)
        => canMaster.OnCanMessage((ushort)CanMessageType.M915, (fake, header, _) =>
        {
            bool refuses = header.DstAddress == refusingBoard;
            fake.InjectStandardReply(header, refuses ? CodeResult.Error : CodeResult.Ok,
                                     refuses ? StallDetectionNotSupported : string.Empty);
        });

    /// <summary>Report a stall of one driver as the board carrying it does</summary>
    private static void InjectDriverStall(Bench bench, byte board, byte driver)
    {
        CanMessageEvent stall = default;
        stall.EventType = EventType.DriverStall;
        stall.DeviceNumber = driver;
        byte[] payload = new byte[stall.GetActualDataLength()];
        CanMessageSerializer.Serialize(in stall, payload);
        bench.CanMaster.InjectCanResponse(DuetControlServer.Link.LinkInterface.UnsolicitedTxToken,
                                          (ushort)CanMessageType.Event, board, payload);
    }

    /// <summary>The stall threshold of every driver of a board, in driver order</summary>
    private static Task<int[]> ThresholdsAsync(Bench bench, byte board)
        => bench.Host.ReadModelAsync(model => model.Boards[board].Drivers!
                                                   .Select(driver => driver.Config.StallDetection.Threshold).ToArray());

    /// <summary>The stall detection settings of one driver</summary>
    private static Task<DriverStallDetection> StallAsync(Bench bench, byte board, int driver)
        => bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![driver].Config.StallDetection);
}
