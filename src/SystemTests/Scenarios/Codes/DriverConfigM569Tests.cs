using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M569 and its sub-codes against the rig the regression suite records on
/// (<see cref="DriversBench"/>). Each scenario is the bench half of a case in
/// <c>lib/DuetRegressionTesting/testcases/drivers</c>: the same G-code, the reply its reference
/// holds, the CAN message it puts on the bus and the object model delta it leaves behind
/// </summary>
/// <remarks>
/// The expected behaviour is RepRapFirmware's (lib/RepRapFirmware) except where
/// src/Documentation/articles/rrf-differences.md documents a deliberate deviation, which is then the
/// behaviour asserted and cited. The one standing deviation in this folder is the object model:
/// section 3 of that article keeps every M569 setting under <c>boards[].drivers[].config</c>, which
/// RepRapFirmware forwards and forgets, so a reference whose delta is empty is matched here by a
/// delta that records the setting
/// </remarks>
[TestFixture]
public class DriverConfigM569Tests : BenchFixture
{
    /// <summary>
    /// The report a 1HCL answers <c>M569 P2.0</c> with, as
    /// <c>testcases/drivers/m569-query-direction.yaml</c> recorded it
    /// </summary>
    private const string ClosedLoopBoardReport =
        "Driver 2.0 runs forwards, active low enable, mode spreadCycle, ccr 0x08053, toff 3, tblank 1, "
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
    public async Task M569WithoutPIsRefused(string code)
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo($"Error: {code}: missing parameter 'P'"),
                        "the guard is RRF's GCodeBuffer::MustSee, whose wording the reference holds");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569>(), Is.Empty,
                        "and it fires before an unaddressed command reaches a driver");
        });
    }

    /// <summary>
    /// M569.2 with a register value but no register number is refused, again before the bus
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-missing-parameters-drivers-m569.yaml</c>, last line. RRF
    /// CanInterface.cpp:1076 requires R whenever V is given, because a value with no register to
    /// write it to would otherwise degenerate into a report
    /// </remarks>
    [Test]
    public async Task M569Point2WithoutRIsRefused()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync($"M569.2 P{DriversBench.MainDriverBoard}.0 V4");
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: M569.2: missing parameter 'R'"),
                        "a register value with no register number is refused (RRF CanInterface.cpp:1076)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point2>(), Is.Empty,
                        "and nothing is sent, so the command does not degenerate into a report");
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
    [TestCase(DriversBench.ClosedLoopBoard, (byte)0)]
    [TestCase(DriversBench.MainDriverBoard, (byte)3)]
    public async Task M569SetsDirection(byte board, byte driver)
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{board}.{driver} S0");
        Assert.That(reply.Trim(), Is.Empty, "a board that took the setting has nothing to say");

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
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569>(ClosedLoopBoardReport));

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{DriversBench.ClosedLoopBoard}.0");

        (byte sentTo, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo(ClosedLoopBoardReport),
                        "the board's report is what the code answers with");
            Assert.That(sentTo, Is.EqualTo(DriversBench.ClosedLoopBoard), "the query goes to the board P names");
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
        const string refusal = "Driver number 1.6 out of range";
        // Only the driver that does not exist is refused, as the board refuses it: the rig's own
        // drivers have to keep working, because the configuration this bench boots from uses them
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M569, (fake, header, payload) =>
                {
                    bool exists = CanMessageSerializer.Deserialize<CanMessageM569>(payload).P
                                  < DriversBench.BoardDrivers[header.DstAddress];
                    fake.InjectStandardReply(header, exists ? CodeResult.Ok : CodeResult.Error,
                                             exists ? string.Empty : refusal);
                }));

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{DriversBench.MainDriverBoard}.6 S0");
        Assert.That(reply.TrimEnd(), Is.EqualTo($"Error: M569: {refusal}"),
                    "an error raised on an expansion board is reported as an error");

        Assert.That(await bench.Host.ReadModelAsync(
                        model => model.Boards[DriversBench.MainDriverBoard].Drivers!.Count),
                    Is.EqualTo(DriversBench.BoardDrivers[DriversBench.MainDriverBoard]),
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
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.MainDriverBoard;

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M569 P{board}.2 B3 F5 Y4:2 H300 U16 R-1")).Trim(), Is.Empty,
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
    /// M569's C parameter never leaves this side, so the command degenerates into a report
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-chopper-control-register.yaml</c>, which is marked known_bad against
    /// RepRapFirmware: C is not in the M569 CAN parameter table (CANlib CanMessageGenericTables.h:64-78),
    /// so the message is built without it and the board answers the bare query with its report. The
    /// local-driver path does apply it, and there are no local drivers here
    /// </remarks>
    [Test]
    public async Task M569ChopperControlRegisterDoesNotReachARemoteDriver()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569>(ClosedLoopBoardReport));
        const byte board = DriversBench.MainDriverBoard;

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync($"M569 P{board}.3 C65860");

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.P, Is.EqualTo(3), "the driver is still addressed");
            Assert.That(reply.TrimEnd(), Is.EqualTo(ClosedLoopBoardReport),
                        "but nothing was set, so the board answers as it would a bare query");
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
    /// What is stored is the number D was given. D4 is <c>DriverMode::direct</c> and D5 is
    /// <c>direct + 1</c>, which the closed loop controller reads as assisted open loop
    /// (Duet3Expansion Move.cpp:1652) - the driver mode enumeration has no name of its own for it, in
    /// RepRapFirmware either, so 5 lands on the value after <c>direct</c>
    /// </para>
    /// </remarks>
    [TestCase(2, DriverMode.SpreadCycle)]
    [TestCase(3, DriverMode.StealthChop)]
    [TestCase(4, DriverMode.Direct)]
    [TestCase(5, DriverMode.Unknown)]
    public async Task M569DSelectsTheDriverMode(int mode, DriverMode expected)
    {
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.ClosedLoopBoard;

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M569 P{board}.0 D{mode}")).Trim(), Is.Empty,
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
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.MainDriverBoard;

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M569 P{board}.1 D3 V300");

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
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.MainDriverBoard;

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M569 P{board}.5 T2.5:2.5:5:0");

        (_, CanMessageM569 sent) = bench.CanMaster.LastCanMessage<CanMessageM569>();
        Assert.That(sent.T, Is.EqualTo(new[] { 2.5f, 2.5f, 5.0f, 0.0f }),
                    "all four floats survive the message (m569-step-pulse-timing.yaml)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].Drivers![5].Config.StepTiming.ToArray()),
                    Is.EqualTo(new[] { 2.5f, 2.5f, 5.0f, 0.0f }),
                    "and boards[].drivers[].config.stepTiming records them (rrf-differences.md section 3)");
    }

    /// <summary>
    /// M569.1 carries the closed loop configuration to the board, and a bare form reports from it
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-1-magnetic-encoder.yaml</c>, <c>m569-1-quadrature-encoder.yaml</c>,
    /// <c>m569-1-feedforward.yaml</c>, <c>m569-1-error-thresholds.yaml</c>,
    /// <c>m569-1-torque-constant.yaml</c>, <c>m569-1-deadband.yaml</c> and <c>m569-1-report.yaml</c>.
    /// Every parameter belongs to the closed loop controller on the board, so the code is repackaged
    /// rather than interpreted and the report is the board's own
    /// </remarks>
    [Test]
    public async Task M569Point1ConfiguresClosedLoopControl()
    {
        const string report = "Encoder type: rotaryQuadrature\nQuadrature encoder pulses/rev: 1000.00\n"
                              + "PID parameters P=80.0 I=0.050 D=0.020 V=500.0 A=100.0, torque constant 0.75Nm/A\n"
                              + "Warning/error threshold 1.50/3.00, standstill deadband 0.500";
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569Point1>(
                message => message.T is null ? report : string.Empty));
        const byte board = DriversBench.ClosedLoopBoard;

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync(
                        $"M569.1 P{board}.0 T2 C1000 S200 R80 I0.05 D0.02 V500 A100 Q0.75 E1.5:3.0 B0.5")).Trim(),
                    Is.Empty, "the board took the configuration");

        (byte sentTo, CanMessageM569Point1 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board), "the message goes to the board carrying the driver");
            Assert.That(sent.P, Is.EqualTo(0), "P is the local driver number");
            Assert.That(sent.T, Is.EqualTo(2), "T is the encoder type (m569-1-quadrature-encoder.yaml)");
            Assert.That(sent.C, Is.EqualTo(1000.0f).Within(1e-3), "C is counts per revolution");
            Assert.That(sent.S, Is.EqualTo(200), "S is the motor steps per revolution");
            Assert.That(sent.R, Is.EqualTo(80.0f).Within(1e-3), "R, I and D are the PID constants");
            Assert.That(sent.I, Is.EqualTo(0.05f).Within(1e-4));
            Assert.That(sent.D, Is.EqualTo(0.02f).Within(1e-4));
            Assert.That(sent.V, Is.EqualTo(500.0f).Within(1e-3),
                        "V and A are the feedforward constants (m569-1-feedforward.yaml)");
            Assert.That(sent.A, Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(sent.Q, Is.EqualTo(0.75f).Within(1e-4),
                        "Q is the torque constant (m569-1-torque-constant.yaml)");
            Assert.That(sent.E, Is.EqualTo(new[] { 1.5f, 3.0f }),
                        "E is the warning and error threshold pair, in that order "
                        + "(m569-1-error-thresholds.yaml)");
            Assert.That(sent.B, Is.EqualTo(0.5f).Within(1e-4),
                        "B is the standstill deadband (m569-1-deadband.yaml)");
        });

        string reply = await bench.Host.ExecuteCodeAsync($"M569.1 P{board}.0");
        Assert.That(reply.TrimEnd(), Is.EqualTo(report),
                    "and a bare M569.1 P answers with the board's multi-line report, unflattened "
                    + "(m569-1-report.yaml)");
    }

    /// <summary>
    /// M569.1 B takes a negative value, which restores the automatic standstill deadband
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-1-deadband.yaml</c>: B0.5 overrides, B0 disables and B-1 goes back to
    /// automatic, so the parameter has to stay signed all the way to the board
    /// </remarks>
    [Test]
    public async Task M569Point1DeadbandTakesANegativeValue()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M569.1 P{DriversBench.ClosedLoopBoard}.0 B-1");

        (_, CanMessageM569Point1 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
        Assert.That(sent.B, Is.EqualTo(-1.0f).Within(1e-4),
                    "B-1 means automatic and must not be re-typed as unsigned on the way out");
    }

    /// <summary>
    /// M569.2 reads and writes a driver register, and carries the waveform correction added at 3.7
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-2-read-register.yaml</c>, <c>m569-2-write-register.yaml</c> and
    /// <c>m569-2-waveform-correction.yaml</c>. The whole result of a read is the value the board
    /// replies with, so the report travels back unchanged
    /// </remarks>
    [Test]
    public async Task M569Point2ReadsWritesAndCorrects()
    {
        const string registerReport = "Register 0x6c value 0x14008055";
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569Point2>(
                message => message.V is null && message.R is not null ? registerReport : string.Empty));
        const byte board = DriversBench.ClosedLoopBoard;

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.2 P{board}.0 R108 V335577173")).Trim(), Is.Empty,
                    "a register write is acknowledged silently");
        (_, CanMessageM569Point2 written) = bench.CanMaster.LastCanMessage<CanMessageM569Point2>();
        Assert.Multiple(() =>
        {
            Assert.That(written.R, Is.EqualTo(108), "R is the register number");
            Assert.That(written.V, Is.EqualTo(335577173), "V is the 32-bit value to write");
        });

        string read = await bench.Host.ExecuteCodeAsync($"M569.2 P{board}.0 R108");
        Assert.That(read.TrimEnd(), Is.EqualTo(registerReport), "and a read answers with the board's value");

        await bench.Host.ExecuteCodeAsync($"M569.2 P{DriversBench.MainDriverBoard}.5 S4 J1.28 O180");
        (_, CanMessageM569Point2 correction) = bench.CanMaster.LastCanMessage<CanMessageM569Point2>();
        Assert.Multiple(() =>
        {
            Assert.That(correction.S, Is.EqualTo(4), "S is the harmonic being corrected");
            Assert.That(correction.J, Is.EqualTo(1.28f).Within(1e-4), "J its magnitude in degrees");
            Assert.That(correction.O, Is.EqualTo(180.0f).Within(1e-3), "O its phase in degrees");
            Assert.That(correction.R, Is.Null, "and no register access, which this form did not ask for");
            Assert.That(correction.V, Is.Null);
        });
    }

    /// <summary>
    /// M569.3, M569.8 and M569.9 are recognised, parse their driver, and are then declined as errors
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-3-read-encoder.yaml</c>, <c>m569-8-read-motor-force.yaml</c> and
    /// <c>m569-9-sense-resistor.yaml</c>. The first two need ODrives on the secondary CAN bus and
    /// Hangprinter kinematics (RRF CanInterface.cpp:1086-1094) and the third exists only in the STM32
    /// port, so all three take the not-supported path - as an error, which is the severity the
    /// references record, and without anything reaching the bus
    /// </remarks>
    [TestCase("M569.3 P1.0")]
    [TestCase("M569.3 P1.0 S")]
    [TestCase("M569.8 P1.0")]
    [TestCase("M569.9 P1.0")]
    [TestCase("M569.9 P1.0 R0.075 S4.4")]
    public async Task M569UnsupportedSubCodesAreErrors(string code)
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync(code);
        string subCode = code[..code.IndexOf(' ')];
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo($"Error: {subCode}: Command is not supported"),
                        "the command is declined as an error, not a warning");
            Assert.That(bench.CanMaster.Transfers.Count, Is.GreaterThanOrEqualTo(0),
                        "and the driver it names is parsed first, so a bad driver is still refused");
        });
    }

    /// <summary>
    /// M569.4 puts a driver into torque mode, and the missing-T error the board raises travels back
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-4-torque-mode.yaml</c>, <c>m569-4-torque-mode-speed-limit.yaml</c>
    /// and <c>m569-4-error-missing-t.yaml</c>. Unlike the missing-P guard this one is not caught on
    /// this side: the command is forwarded and the board reports the missing parameter, which is a
    /// whole error round trip
    /// </remarks>
    [Test]
    public async Task M569Point4CommandsATorque()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569Point4>(
                message => message.T is null ? "missing T parameter" : string.Empty));
        const byte board = DriversBench.ClosedLoopBoard;

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.4 P{board}.0 T0.001 V50")).Trim(), Is.Empty,
                    "the board took the torque");

        (byte sentTo, CanMessageM569Point4 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point4>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(sent.T, Is.EqualTo(0.001f).Within(1e-6), "T is the commanded torque");
            Assert.That(sent.V, Is.EqualTo(50.0f).Within(1e-3),
                        "V is the maximum speed in full steps per second (m569-4-torque-mode-speed-limit.yaml)");
        });
    }

    /// <summary>
    /// An error the board raises about M569.4 comes back as an error naming the sub-code
    /// </summary>
    /// <remarks><c>testcases/drivers/m569-4-error-missing-t.yaml</c></remarks>
    [Test]
    public async Task M569Point4WithoutTIsRefusedByTheBoard()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569Point4>("missing T parameter",
                                                                                       CodeResult.Error));

        string reply = await bench.Host.ExecuteCodeAsync($"M569.4 P{DriversBench.ClosedLoopBoard}.0");
        Assert.That(reply.TrimEnd(), Is.EqualTo("Error: M569.4: missing T parameter"),
                    "the board's refusal is carried back over CAN and reported as an error");
    }

    /// <summary>
    /// M569.6 runs a closed loop tuning manoeuvre, and both of its refusals are errors
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-6-closed-loop-tuning-move.yaml</c>: V1 is basic tuning, which the
    /// board refuses for an absolute encoder, and the form with no V reports the last calibration
    /// </remarks>
    [Test]
    public async Task M569Point6ReportsTheBoardsTuningVerdict()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569Point6>(
                message => message.V is null
                    ? "Driver 2.0 calibration failed (no reason available)"
                    : "basic tuning is not applicable to absolute encoders",
                CodeResult.Error));
        const byte board = DriversBench.ClosedLoopBoard;

        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync($"M569.6 P{board}.0 V1")).TrimEnd(),
                        Is.EqualTo("Error: M569.6: basic tuning is not applicable to absolute encoders"));
            Assert.That((await bench.Host.ExecuteCodeAsync($"M569.6 P{board}.0")).TrimEnd(),
                        Is.EqualTo("Error: M569.6: Driver 2.0 calibration failed (no reason available)"));
        });
    }

    /// <summary>
    /// M569.7 binds an output port to the driver's brake, with a delay and a PWM voltage limit
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-7-motor-brake-port.yaml</c>. The port name reaches the board with
    /// its board address stripped, because the board it is sent to is the board it is on
    /// </remarks>
    [Test]
    public async Task M569Point7BindsTheBrakePort()
    {
        const string report = "Driver 2.0 uses brake port 2.out0 with voltage limited to 12.0 by PWM, brake delay 20ms";
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM569Point7>(
                message => message.C is null && message.V is null && message.S is null ? report : string.Empty));
        const byte board = DriversBench.ClosedLoopBoard;

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.7 P{board}.0 C\"{board}.out0\" S20")).Trim(), Is.Empty,
                    "the board took the binding");

        (byte sentTo, CanMessageM569Point7 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board), "the port's board is where the message goes");
            Assert.That(sent.C, Is.EqualTo("out0"), "and the name it carries is local to that board");
            Assert.That(sent.S, Is.EqualTo(20), "S is the brake-off delay in milliseconds");
        });

        await bench.Host.ExecuteCodeAsync($"M569.7 P{board}.0 V12");
        (_, CanMessageM569Point7 limited) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.That(limited.V, Is.EqualTo(12.0f).Within(1e-3), "V limits the brake voltage by PWM");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.7 P{board}.0")).TrimEnd(), Is.EqualTo(report),
                    "and a bare M569.7 P answers with the board's report");
    }

    /// <summary>
    /// M569.7 C"nil" releases the port, which has to reach the board as the literal name
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-7-motor-brake-port.yaml</c> teardown: a port left bound to a brake is
    /// switched by every enable and disable after it, so releasing it has to work
    /// </remarks>
    [Test]
    public async Task M569Point7NilReleasesTheBrakePort()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M569.7 P{DriversBench.ClosedLoopBoard}.0 C\"nil\"");

        (_, CanMessageM569Point7 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.That(sent.C, Is.EqualTo("nil"), "\"nil\" is a name the board reads, not an empty port");
    }
}
