using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569 against the rig the regression suite records on
/// (<see cref="DriversRig"/>). Each scenario is the bench half of a case in
/// <c>lib/DuetRegressionTesting/testcases/drivers</c>: the same G-code, the reply its reference
/// holds, the CAN message it puts on the bus and the object model delta it leaves behind
/// </summary>
/// <remarks>
/// The expected behaviour is RepRapFirmware's (src/RepRapFirmware) except where
/// src/Documentation/articles/rrf-differences.md documents a deliberate deviation, which is then the
/// behaviour asserted and cited. The one standing deviation in this folder is the object model:
/// section 3 of that article keeps every M569 setting under <c>boards[].drivers[].config</c>, which
/// RepRapFirmware forwards and forgets, so a reference whose delta is empty is matched here by a
/// delta that records the setting
/// </remarks>
[TestFixture]
[GCode("M569")]
public class M569Tests : BenchFixture
{
    /// <summary>
    /// The report a 1HCL answers <c>M569 P2.0</c> with, as
    /// <c>testcases/drivers/m569-query-direction.yaml</c> recorded it
    /// </summary>
    private static readonly string ClosedLoopBoardReport =
        $"Driver {DriversRig.ClosedLoopDriver(0)} runs forwards, active low enable, mode spreadCycle, ccr 0x08053, toff 3, tblank 1, "
        + "thigh 200 (46.9 mm/sec), gs 32, iRun/iHold 0/0, current 25.4, hstart/hend/hdec 5/0/0, pos 264";

    /// <summary>
    /// M569 with no P is refused before anything reaches the bus, in RepRapFirmware's wording
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-missing-parameters-drivers-m569.yaml</c>. Every sub-code shares the
    /// guard (RRF GCodes3.cpp:1046 <c>gb.MustSee('P')</c>), which reports through
    /// GCodeBuffer::MustSee as "missing parameter 'P'"
    /// </remarks>
    [TestCase("M569")]
    [TestCase("M569.1")]
    [TestCase("M569.3")]
    [Category("KnownGap")]
    public async Task M569WithoutPIsRefused(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(CodeErrors.MissingParameter('P')),
                        "the guard is RRF's GCodeBuffer::MustSee, whose wording the reference holds");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569>(), Is.Empty,
                        "and it fires before an unaddressed command reaches a driver");
        });
    }

    /// <summary>
    /// M569 P&lt;board&gt;.&lt;driver&gt; S sets the direction: the board gets an m569 message carrying
    /// P and S, and the direction is recorded under boards[].drivers[].config
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-set-direction.yaml</c> and <c>m569-set-direction-6hc.yaml</c>, whose
    /// deltas are <c>boards[2].drivers[0].config.direction 1 -&gt; 0</c> and
    /// <c>boards[1].drivers[3].config.direction 1 -&gt; 0</c>. boards[] is indexed by CAN address
    /// with the main board at index 0, as RepRapFirmware's is (RepRap.cpp objectModelArrayTable entry 0)
    /// </remarks>
    [TestCase(DriversRig.ClosedLoopBoard, (byte)0)]
    [TestCase(DriversRig.MainDriverBoard, (byte)3)]
    public async Task M569SetsDirection(byte board, byte driver)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{board}.{driver} S0");
        Assert.That(reply, Reply.IsEmpty, "a board that took the setting has nothing to say");

        (byte sentTo, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board), "the message goes to the board named by P");
            Assert.That(sent.P, Is.EqualTo(driver), "carrying the driver number local to that board");
            Assert.That(sent.S, Is.EqualTo(0), "and the direction it was given");
        });

        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![driver].Config.Direction),
                    Is.False,
                    "boards[<address>].drivers[].config.direction records it (rrf-differences.md section 3)");
    }

    /// <summary>
    /// A bare M569 P is a query: the board answers with its own report, which travels back unchanged
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-query-direction.yaml</c>. Nothing is interpreted on this side, so the
    /// reply is exactly the driver report the board sent
    /// </remarks>
    [Test]
    public async Task M569QueryReturnsTheBoardsReport()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569>(ClosedLoopBoardReport))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(DriversRig.ClosedLoopBoard, 0)}");

        (byte sentTo, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo(ClosedLoopBoardReport),
                        "the board's report is what the code answers with");
            Assert.That(sentTo, Is.EqualTo(DriversRig.ClosedLoopBoard), "the query goes to the board P names");
            Assert.That(sent.P, Is.EqualTo(0), "carrying only the driver number");
            Assert.That(sent.S, Is.Null, "and nothing else, so the board reports rather than sets");
        });
    }

    /// <summary>
    /// A driver one past the end of a board that does exist is refused by the board, and the error it
    /// raises comes back as an error rather than being swallowed
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-error-unknown-driver.yaml</c>: the 6HC has drivers 0 to 5, so
    /// <c>M569 P1.6</c> reaches the board and is rejected there with
    /// "Driver number 1.6 out of range"
    /// </remarks>
    [Test]
    public async Task M569OnAnUnknownDriverReportsTheBoardsRefusal()
    {
        string refusal = $"Driver number {DriversRig.MainDriver(6)} out of range";
        // Only the driver that does not exist is refused, as the board refuses it: the rig's own
        // drivers have to keep working, because the configuration this bench boots from uses them
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M569, (fake, header, payload) =>
                {
                    bool exists = CanMessageSerializer.Deserialize<CanMessageM569>(payload).P
                                  < DriversRig.BoardDrivers[header.DstAddress];
                    fake.InjectStandardReply(header, exists ? CodeResult.Ok : CodeResult.Error,
                                             exists ? string.Empty : refusal);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{DriversRig.MainDriver(6)} S0");
        Assert.That(reply, Reply.IsError(refusal),
                    "an error raised on an expansion board is reported as an error");

        Assert.That(await bench.Host.ReadModelAsync(
                        model => model.Boards[DriversRig.MainDriverBoard].Drivers!.Count),
                    Is.EqualTo(DriversRig.BoardDrivers[DriversRig.MainDriverBoard]),
                    "and nothing is recorded for it, so the board does not gain a driver it has not "
                    + "got (RRF GCodes3.cpp ConfigureDriver stores only what the board took)");
    }

    /// <summary>
    /// The M569 parameters that configure the chopper, each reaching the board in its own field and
    /// each recorded under boards[].drivers[].config
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-blanking-time.yaml</c>, <c>m569-chopper-off-time.yaml</c>,
    /// <c>m569-hysteresis.yaml</c>, <c>m569-thigh.yaml</c>, <c>m569-irun-scaler.yaml</c> and
    /// <c>m569-enable-polarity.yaml</c>. RepRapFirmware forwards them and keeps nothing; the object
    /// model entries are rrf-differences.md section 3
    /// </remarks>
    [Test]
    public async Task M569ChopperParametersReachTheDriverAndAreRecorded()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 2)} B3 F5 Y4:2 H300 U16 R-1"), Reply.IsEmpty,
                    "the board took every field");

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.B, Is.EqualTo(3), "B is the blanking time");
            Assert.That(sent.F, Is.EqualTo(5), "F is the chopper off time");
            Assert.That(sent.Y, Is.EqualTo(new uint[] { 4, 2 }), "Y is hysteresis start and end");
            Assert.That(sent.H, Is.EqualTo(300), "H is the thigh threshold");
            Assert.That(sent.U, Is.EqualTo(16), "U pins the iRun current scaler");
            Assert.That(sent.R, Is.EqualTo(-1),
                        "and R-1, the only negative value M569 takes, survives as a signed field "
                        + "(m569-enable-polarity.yaml)");
        });

        DriverConfig config = await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![2].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.BlankingTime, Is.EqualTo(3));
            Assert.That(config.OffTime, Is.EqualTo(5));
            Assert.That(config.Hysteresis.Start, Is.EqualTo(4));
            Assert.That(config.Hysteresis.End, Is.EqualTo(2));
            Assert.That(config.CoolStepThreshold, Is.EqualTo(300));
            Assert.That(config.CurrentScaler, Is.EqualTo(16));
            Assert.That(config.EnablePolarity, Is.EqualTo(-1));
        });
    }

    /// <summary>
    /// M569 C writes the chopper control register, which is recorded along with the off time,
    /// blanking time and hysteresis it contains
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-chopper-control-register.yaml</c>: its reference, recorded on 3.7.0,
    /// carries C in the message (paramMap 0x00801), the board answers with nothing, and the report
    /// after it reads "ccr 0x010144, toff 4, tblank 2, ... hstart/hend/hdec 4/2/0". C has been in the
    /// M569 parameter table since 3.7.0-rc.2 (CANlib CanMessageGenericTables.h); the case's known_bad
    /// mark describes rc.1
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569CSetsTheChopperControlRegister()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 3)} C65860"), Reply.IsEmpty,
                    "the board took the register");

        (byte sentTo, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board), "the message goes to the board P names");
            Assert.That(sent.P, Is.EqualTo(3), "carrying the local driver number");
            Assert.That(sent.C, Is.EqualTo(65860u), "and the whole register value in C");
        });

        DriverConfig config = await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![3].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.ChopperControl, Is.EqualTo(65860u), "boards[].drivers[].config.chopperControl records it");
            Assert.That(config.OffTime, Is.EqualTo(4), "and the fields it contains, as the board reports them: toff 4");
            Assert.That(config.BlankingTime, Is.EqualTo(2), "tblank 2");
            Assert.That(config.Hysteresis.Start, Is.EqualTo(4), "hstart 4");
            Assert.That(config.Hysteresis.End, Is.EqualTo(2), "hend 2");
        });
    }

    /// <summary>
    /// F, B and Y after C change their fields of the recorded register, so the model never holds a
    /// register that disagrees with the fields
    /// </summary>
    /// <remarks>
    /// The board applies F, B and Y to the register C set (Duet3Expansion TmcDriverState::SetRegister),
    /// so a board reconfigured from the model has to be given the register as it now is. The values
    /// are those of <c>m569-chopper-off-time.yaml</c>, <c>m569-blanking-time.yaml</c> and
    /// <c>m569-hysteresis.yaml</c> applied to the default register 0x008053
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569FBAndYChangeTheRecordedChopperControlRegister()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        string driver = Expansion.Driver(DriversRig.MainDriverBoard, 2);

        await bench.Host.ExecuteCodeAsync($"M569 P{driver} C{0x008053}");
        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{driver} F5 B3 Y4:2"), Reply.IsEmpty);

        DriverConfig config = await bench.Host.ReadModelAsync(model => model.Boards[DriversRig.MainDriverBoard].Drivers![2].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.ChopperControl, Is.EqualTo(0x018145u), "toff 5, tblank 3 and hstart/hend 4/2 in the register");
            Assert.That(config.OffTime, Is.EqualTo(5));
            Assert.That(config.BlankingTime, Is.EqualTo(3));
            Assert.That(config.Hysteresis.Start, Is.EqualTo(4));
            Assert.That(config.Hysteresis.End, Is.EqualTo(2));
        });

        await bench.Host.ExecuteCodeAsync($"M569 P{driver} C65860");
        config = await bench.Host.ReadModelAsync(model => model.Boards[DriversRig.MainDriverBoard].Drivers![2].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.ChopperControl, Is.EqualTo(65860u), "a later C replaces the whole register");
            Assert.That(config.OffTime, Is.EqualTo(4), "and the fields F, B and Y had set with it");
            Assert.That(config.BlankingTime, Is.EqualTo(2));
            Assert.That(config.Hysteresis.Start, Is.EqualTo(4));
            Assert.That(config.Hysteresis.End, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// F, B and Y without a C before them are recorded on their own, with no register to change
    /// </summary>
    [Test]
    public async Task M569FieldsWithoutCLeaveTheRegisterUnset()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(DriversRig.MainDriverBoard, 2)} F5");
        DriverConfig config = await bench.Host.ReadModelAsync(model => model.Boards[DriversRig.MainDriverBoard].Drivers![2].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.OffTime, Is.EqualTo(5));
            Assert.That(config.ChopperControl, Is.Null,
                        "the rest of the register is the board's default, which this side does not know");
        });
    }

    /// <summary>
    /// Y with three values sets the hysteresis decrement too
    /// </summary>
    /// <remarks><c>testcases/drivers/m569-hysteresis.yaml</c> uses the two-value form; the third is hdec</remarks>
    [Test]
    public async Task M569YWithThreeValuesSetsTheDecrement()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 4)} Y4:2:1"), Reply.IsEmpty);

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.That(sent.Y, Is.EqualTo(new uint[] { 4, 2, 1 }), "all three values reach the board");

        DriverHysteresis hysteresis = await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![4].Config.Hysteresis);
        Assert.Multiple(() =>
        {
            Assert.That(hysteresis.Start, Is.EqualTo(4));
            Assert.That(hysteresis.End, Is.EqualTo(2));
            Assert.That(hysteresis.Decrement, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// M569 R sets the enable polarity: 1 active high, 0 active low, -1 always disabled
    /// </summary>
    /// <remarks><c>testcases/drivers/m569-enable-polarity.yaml</c> sends R1, R-1 and, in its teardown, R0</remarks>
    [TestCase(1)]
    [TestCase(0)]
    [TestCase(-1)]
    public async Task M569RSetsTheEnablePolarity(int polarity)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 0)} R{polarity}"), Reply.IsEmpty);

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.That(sent.R, Is.EqualTo(polarity), "R reaches the board as a signed value");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![0].Config.EnablePolarity),
                    Is.EqualTo(polarity), "and boards[].drivers[].config.enablePolarity records it");
    }

    /// <summary>
    /// A current scaler below 16 is taken with a warning from the board, and is recorded because the
    /// board took it
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-irun-scaler.yaml</c> sends U16 and U31, which the board takes
    /// silently. Below 16 Duet3Expansion Move::ProcessM569 applies the value and returns
    /// <c>GCodeResult::warning</c>, and RepRapFirmware stores what it stores under
    /// <c>res &lt;= GCodeResult::warning</c> (GCodes3.cpp ConfigureDriver)
    /// </remarks>
    [Test]
    public async Task M569ULowCurrentScalerIsAWarningAndIsRecorded()
    {
        const byte board = DriversRig.MainDriverBoard;
        string warning = $"Current scaler = 8 for driver 0 might result in poor microstep performance. Recommended minimum is 16.";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569>(
                message => message.U is >= 0 and < 16 ? warning : string.Empty,
                CodeResult.Warning)).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 0)} U8");
        Assert.That(reply, Reply.IsWarning(warning), "the board's warning is reported as a warning");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![0].Config.CurrentScaler),
                    Is.EqualTo(8), "and the scaler it applied is recorded");
    }

    /// <summary>
    /// A board that refuses part of an M569 has nothing recorded for any of it
    /// </summary>
    /// <remarks>
    /// Duet3Expansion Move::ProcessM569 refuses a T with two or three values ("bad timing parameter,
    /// expected 1 or 4 values") after it has applied S and R, and RepRapFirmware stores the direction
    /// only when the board did not refuse (GCodes3.cpp ConfigureDriver, <c>res &lt;= GCodeResult::warning</c>)
    /// </remarks>
    [Test]
    public async Task M569RefusedByTheBoardRecordsNothing()
    {
        const byte board = DriversRig.MainDriverBoard;
        const string refusal = "bad timing parameter, expected 1 or 4 values";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M569, (fake, header, payload) =>
                {
                    bool refused = CanMessageSerializer.Deserialize<CanMessageM569>(payload).T is { Length: not (1 or 4) };
                    fake.InjectStandardReply(header, refused ? CodeResult.Error : CodeResult.Ok, refused ? refusal : string.Empty);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 0)} S0 T2:2");
        Assert.That(reply, Reply.IsError(refusal), "the board's refusal is the code's error");

        DriverConfig config = await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![0].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.Direction, Is.True, "the direction stays as the rig's config set it");
            Assert.That(config.StepTiming, Is.Empty, "and no timing is recorded");
        });
    }

    /// <summary>
    /// A driver on a board that is not on the bus is an error naming the board, and nothing is recorded
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-bad-driver.yaml</c>, last line: <c>M569 P99.0</c> sits out the
    /// response timeout and its reference reads "Error: M569: Timed out awaiting response from board
    /// 99 to request type 6018". The case's known_bad mark says RepRapFirmware reports this at
    /// severity ok, which the reference recorded on 3.7.0 does not show
    /// </remarks>
    [Test]
    public async Task M569OnABoardThatIsNotOnTheBusIsAnError()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        bench.CanMaster.ScriptCanSendStatus(CanStatus.ResponseTimeout);
        string reply = await bench.Host.ExecuteCodeAsync("M569 P99.0 S0", timeoutMs: 5_000);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsError("board 99"), "a board that does not answer is an error naming the board");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards.Any(b => b?.CanAddress == 99)),
                        Is.False, "and no board is created for it");
        });
    }

    /// <summary>
    /// A driver on board 0 is refused before anything reaches the bus
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-bad-driver.yaml</c>, first line: <c>M569 P0.99</c> is an error in
    /// RepRapFirmware too, where board 0 has its own drivers and 99 is out of range. Here board 0 runs
    /// DuetCANMaster and has no drivers at all, so every driver number on it is refused
    /// </remarks>
    [TestCase(0)]
    [TestCase(99)]
    public async Task M569OnTheMainBoardIsRefused(int driver)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P0.{driver} S0");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(), "the driver is refused as an error");
            Assert.That(bench.CanMaster.CanMessages(), Is.Empty, "before anything is sent");
        });
    }

    /// <summary>
    /// Every driver of every board answers its own query, addressed to its board with its local number
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-report-all-drivers.yaml</c> queries each driver in turn, changing
    /// nothing; each reply is that driver's report
    /// </remarks>
    [Test]
    public async Task M569QueriesEachDriverOfEachBoard()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M569, (fake, header, payload) =>
                {
                    CanMessageM569 message = CanMessageSerializer.Deserialize<CanMessageM569>(payload);
                    fake.InjectStandardReply(header, CodeResult.Ok,
                                             message.S is null ? $"Driver {header.DstAddress}.{message.P} runs forwards" : string.Empty);
                })).StartAsync();

        foreach ((byte board, byte drivers) in DriversRig.BoardDrivers)
        {
            for (int driver = 0; driver < drivers; driver++)
            {
                string reply = await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, driver)}");
                (byte sentTo, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
                Assert.Multiple(() =>
                {
                    Assert.That(reply.TrimEnd(), Is.EqualTo($"Driver {Expansion.Driver(board, driver)} runs forwards"),
                                "the reply is the report of the driver asked about");
                    Assert.That(sentTo, Is.EqualTo(board));
                    Assert.That(sent.P, Is.EqualTo(driver));
                });
            }
        }
    }

    /// <summary>
    /// A list of drivers configures only the first, as RepRapFirmware does for M569 with no sub-code
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp ConfigureDriver reads P as a driver array but leaves its loop after the first
    /// driver unless the sub-code is .3, .4 or .8, and CanMessageGenericConstructor reads only the
    /// first driver of P into the message
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569WithADriverListConfiguresOnlyTheFirst()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 3)}:{Expansion.Driver(board, 4)} S0");
        Assert.That(reply, Reply.IsEmpty);

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569>().Select(sent => sent.Message.P), Is.EqualTo(new byte?[] { 3 }),
                        "one message, for the first driver");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![3].Config.Direction), Is.False);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![4].Config.Direction), Is.True,
                        "the second driver is left alone");
        });
    }

    /// <summary>
    /// M569 D selects the driver mode, and the mode is recorded so the machine can be rebuilt from
    /// the object model
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-driver-mode-spreadcycle.yaml</c>,
    /// <c>m569-driver-mode-stealthchop.yaml</c>, <c>m569-driver-mode-closed-loop.yaml</c> and
    /// <c>m569-driver-mode-assisted-open-loop.yaml</c>, whose deltas are
    /// <c>boards[].drivers[].config.mode</c>. The stealthChop case carries V, the tpwmthrs
    /// changeover threshold, in the same command.
    /// <para>
    /// What is stored is the number D was given, which RepRapFirmware reports as it is
    /// (DriverData::StoreMode): D4 is <c>DriverMode::direct</c>, closed loop, and D5
    /// <c>DriverMode::assistedOpen</c>, whose reference delta is <c>config.mode 2 -&gt; 5</c>
    /// </para>
    /// </remarks>
    [TestCase(2, DriverMode.SpreadCycle)]
    [TestCase(3, DriverMode.StealthChop)]
    [TestCase(4, DriverMode.Direct)]
    [TestCase(5, DriverMode.AssistedOpen)]
    public async Task M569DSelectsTheDriverMode(int mode, DriverMode expected)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.ClosedLoopBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 0)} D{mode}"), Reply.IsEmpty,
                    "the board took the mode");

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.That(sent.D, Is.EqualTo(mode), "D carries the mode number to the board");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![0].Config.Mode),
                    Is.EqualTo(expected),
                    "boards[].drivers[].config.mode records it (rrf-differences.md section 3)");
    }

    /// <summary>
    /// M569 V sets the stealthChop changeover threshold alongside the mode
    /// </summary>
    /// <remarks><c>testcases/drivers/m569-driver-mode-stealthchop.yaml</c> sends D3 and V300 together</remarks>
    [Test]
    public async Task M569VSetsTheStealthChopThreshold()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 1)} D3 V300");

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.D, Is.EqualTo(3), "D3 is stealthChop");
            Assert.That(sent.V, Is.EqualTo(300), "and V is tpwmthrs, in the same message");
        });

        DriverConfig config = await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![1].Config);
        Assert.Multiple(() =>
        {
            Assert.That(config.Mode, Is.EqualTo(DriverMode.StealthChop));
            Assert.That(config.StealthChopThreshold, Is.EqualTo(300));
        });
    }

    /// <summary>
    /// The four-value T form carries every step timing to the board and records all four
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-step-pulse-timing.yaml</c> sends <c>T2.5:2.5:5:0</c>; the board
    /// reports back the rounded-up values it applied, which is its business rather than this side's
    /// </remarks>
    [Test]
    public async Task M569TCarriesTheStepPulseTiming()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 5)} T2.5:2.5:5:0");

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.That(sent.T, Is.EqualTo(new[] { 2.5f, 2.5f, 5.0f, 0.0f }),
                    "all four floats survive the message (m569-step-pulse-timing.yaml)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![5].Config.StepTiming.ToArray()),
                    Is.EqualTo(new[] { 2.5f, 2.5f, 5.0f, 0.0f }),
                    "and boards[].drivers[].config.stepTiming records them (rrf-differences.md section 3)");
    }

    /// <summary>
    /// The single-value T form reaches the board as one value, which stands for all four timings
    /// </summary>
    /// <remarks>
    /// The message carries the value as the code gave it and the board copies it to the other three
    /// (Duet3Expansion Move::ProcessM569, <c>numTimings == 1</c>), so all four are recorded
    /// </remarks>
    [Test]
    public async Task M569TWithOneValueSetsEveryTiming()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 4)} T3"), Reply.IsEmpty);

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.That(sent.T, Is.EqualTo(new[] { 3.0f }), "the single value is sent as it was given");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![4].Config.StepTiming.ToArray()),
                    Is.EqualTo(new[] { 3.0f, 3.0f, 3.0f, 3.0f }), "and recorded as the four timings the board applies");
    }

    /// <summary>
    /// A mode the driver does not support is refused by the board and the recorded mode stays as it was
    /// </summary>
    /// <remarks>
    /// Duet3Expansion Move::ProcessM569 answers "Driver 1.0 does not support mode '...'" when
    /// SmartDrivers::SetDriverMode fails, and RepRapFirmware stores the mode only when the board took
    /// it (GCodes3.cpp ConfigureDriver)
    /// </remarks>
    [Test]
    public async Task M569DRefusedByTheBoardLeavesTheMode()
    {
        const byte board = DriversRig.MainDriverBoard;
        string refusal = $"Driver {Expansion.Driver(board, 0)} does not support mode 'unknown'";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M569, (fake, header, payload) =>
                {
                    bool refused = CanMessageSerializer.Deserialize<CanMessageM569>(payload).D is > 5;
                    fake.InjectStandardReply(header, refused ? CodeResult.Error : CodeResult.Ok, refused ? refusal : string.Empty);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{Expansion.Driver(board, 0)} D7");
        Assert.That(reply, Reply.IsError(refusal));
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![0].Config.Mode),
                    Is.EqualTo(DriverConfig.DefaultDriverMode), "the mode the driver had is still recorded");
    }
}
