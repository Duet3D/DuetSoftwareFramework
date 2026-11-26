using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.1, closed loop configuration: the encoder, the PID and feedforward constants, the thresholds,
/// the torque constant and the deadband, which the closed loop board applies and reports
/// </summary>
/// <remarks>
/// RRF CanInterface::ConfigureRemoteDriver case 1 repackages the code for the board, and Duet3Expansion's
/// ClosedLoop::ProcessM569Point1 validates and applies it. What the board took is also recorded under
/// <c>boards[].drivers[].config.closedLoop</c> (rrf-differences.md section 3)
/// </remarks>
[TestFixture]
[GCode("M569.1")]
public class M569Point1Tests : BenchFixture
{
    /// <summary>The report a 1HCL with a quadrature encoder answers a bare M569.1 with, as <c>m569-1-report.yaml</c> recorded it</summary>
    private const string QuadratureReport = "Encoder type: rotaryQuadrature\nQuadrature encoder pulses/rev: 1000.00\n"
                                            + "PID parameters P=30.0 I=0.000 D=0.000 V=1000.0 A=0.0, torque constant 1.00Nm/A\n"
                                            + "Warning/error threshold 2.00/4.00, standstill deadband 0.050 (auto)";

    /// <summary>
    /// Every parameter reaches the board in its own field, and what the board took is recorded
    /// </summary>
    /// <remarks>
    /// <c>m569-1-quadrature-encoder.yaml</c>, <c>m569-1-magnetic-encoder.yaml</c>, <c>m569-1-feedforward.yaml</c>,
    /// <c>m569-1-error-thresholds.yaml</c>, <c>m569-1-torque-constant.yaml</c> and <c>m569-1-deadband.yaml</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1ConfiguresClosedLoopControl()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.ClosedLoopBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync(
                        $"M569.1 P{Expansion.Driver(board, 0)} T2 C1000 S200 R80 I0.05 D0.02 V500 A100 Q0.75 E1.5:3.0 B0.5"),
                    Reply.IsEmpty, "the board took the configuration");

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
            Assert.That(sent.V, Is.EqualTo(500.0f).Within(1e-3), "V and A are the feedforward constants (m569-1-feedforward.yaml)");
            Assert.That(sent.A, Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(sent.Q, Is.EqualTo(0.75f).Within(1e-4), "Q is the torque constant (m569-1-torque-constant.yaml)");
            Assert.That(sent.E, Is.EqualTo(new[] { 1.5f, 3.0f }),
                        "E is the warning and error threshold pair, in that order (m569-1-error-thresholds.yaml)");
            Assert.That(sent.B, Is.EqualTo(0.5f).Within(1e-4), "B is the standstill deadband (m569-1-deadband.yaml)");
            Assert.That(sent.Y, Is.Null, "and nothing the code did not carry");
        });

        DriverClosedLoopConfig config = await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.ClosedLoop);
        Assert.Multiple(() =>
        {
            Assert.That(config.EncoderType, Is.EqualTo(EncoderType.RotaryQuadrature));
            Assert.That(config.CountsPerRev, Is.EqualTo(1000.0f));
            Assert.That(config.StepsPerRev, Is.EqualTo(200));
            Assert.That(config.MagneticEncoderType, Is.Null);
            Assert.That(config.Kp, Is.EqualTo(80.0f));
            Assert.That(config.Ki, Is.EqualTo(0.05f));
            Assert.That(config.Kd, Is.EqualTo(0.02f));
            Assert.That(config.VelocityFeedForward, Is.EqualTo(500.0f));
            Assert.That(config.AccelerationFeedForward, Is.EqualTo(100.0f));
            Assert.That(config.TorqueConstant, Is.EqualTo(0.75f));
            Assert.That(config.WarningThreshold, Is.EqualTo(1.5f));
            Assert.That(config.ErrorThreshold, Is.EqualTo(3.0f));
            Assert.That(config.Deadband, Is.EqualTo(0.5f));
        });
    }

    /// <summary>
    /// A bare M569.1 P carries only the driver, answers with the board's multi-line report and changes nothing
    /// </summary>
    /// <remarks><c>m569-1-report.yaml</c>, whose request has paramMap 0x00001</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1ReportsTheBoardsConfiguration()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point1>(
                message => message.T is null ? QuadratureReport : string.Empty)).StartAsync();
        const byte board = DriversRig.ClosedLoopBoard;
        await bench.Host.ExecuteCodeAsync($"M569.1 P{Expansion.Driver(board, 0)} T2 C1000 S200");

        string reply = await bench.Host.ExecuteCodeAsync($"M569.1 P{Expansion.Driver(board, 0)}");

        (_, CanMessageM569Point1 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
        Assert.Multiple(async () =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo(QuadratureReport), "the report travels back unflattened");
            Assert.That(sent.Generic.ParamMap, Is.EqualTo(1u), "the request carries P alone");
            Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.ClosedLoop.CountsPerRev),
                        Is.EqualTo(1000.0f), "and the configuration is left as it was");
        });
    }

    /// <summary>
    /// B0.5 overrides the deadband, B0 turns it off and a negative B restores the automatic deadband, which is
    /// recorded as no deadband having been configured
    /// </summary>
    /// <remarks><c>m569-1-deadband.yaml</c>: the parameter has to stay signed all the way to the board</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1DeadbandTakesANegativeValue()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        string driver = DriversRig.ClosedLoopDriver(0);

        await bench.Host.ExecuteCodeAsync($"M569.1 P{driver} B0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.ClosedLoopBoard)!.Drivers![0].Config.ClosedLoop.Deadband),
                    Is.EqualTo(0.0f), "B0 is a deadband of zero, not automatic");

        await bench.Host.ExecuteCodeAsync($"M569.1 P{driver} B-1");
        (_, CanMessageM569Point1 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
        Assert.That(sent.B, Is.EqualTo(-1.0f).Within(1e-4), "B-1 means automatic and must not be re-typed as unsigned on the way out");
        Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.ClosedLoopBoard)!.Drivers![0].Config.ClosedLoop.Deadband),
                    Is.Null, "and the automatic deadband is what an unconfigured driver has");
    }

    /// <summary>
    /// Y names the magnetic encoder and travels with T, and H, which the CAN table no longer carries, is dropped
    /// so that a code with only H is a report
    /// </summary>
    /// <remarks><c>m569-1-holding-current-and-encoder-type.yaml</c>; CANlib CanMessageGenericTables.h retires H as 'h'</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1CarriesTheMagneticEncoderTypeAndDropsH()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.ClosedLoopBoard;

        await bench.Host.ExecuteCodeAsync($"M569.1 P{Expansion.Driver(board, 0)} T3 S200 Y\"as5047d\"");
        (_, CanMessageM569Point1 typed) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
        Assert.Multiple(() =>
        {
            Assert.That(typed.T, Is.EqualTo(3));
            Assert.That(typed.Y, Is.EqualTo("as5047d"), "Y reaches the board as the encoder's name");
            Assert.That(typed.C, Is.Null, "a magnetic encoder has no counts per revolution to send");
        });

        await bench.Host.ExecuteCodeAsync($"M569.1 P{Expansion.Driver(board, 0)} H10");
        (_, CanMessageM569Point1 holding) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
        Assert.That(holding.Generic.ParamMap, Is.EqualTo(1u), "H is not carried, so the request is a bare report");

        DriverClosedLoopConfig config = await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.ClosedLoop);
        Assert.Multiple(() =>
        {
            Assert.That(config.EncoderType, Is.EqualTo(EncoderType.RotaryMagnetic));
            Assert.That(config.MagneticEncoderType, Is.EqualTo("as5047d"));
            Assert.That(config.StepsPerRev, Is.EqualTo(200));
            Assert.That(config.CountsPerRev, Is.Null);
        });
    }

    /// <summary>
    /// A new encoder type replaces the encoder, so the counts, steps and magnetic type of the old one are forgotten
    /// </summary>
    /// <remarks>Duet3Expansion ClosedLoop::ProcessM569Point1 builds the new encoder from this code's C, S and Y alone</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1TReplacesTheEncoder()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        string driver = DriversRig.ClosedLoopDriver(0);

        await bench.Host.ExecuteCodeAsync($"M569.1 P{driver} T2 C1000 S400 R50");
        await bench.Host.ExecuteCodeAsync($"M569.1 P{driver} T0");

        DriverClosedLoopConfig config = await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.ClosedLoopBoard)!.Drivers![0].Config.ClosedLoop);
        Assert.Multiple(() =>
        {
            Assert.That(config.EncoderType, Is.EqualTo(EncoderType.None));
            Assert.That(config.CountsPerRev, Is.Null);
            Assert.That(config.StepsPerRev, Is.Null);
            Assert.That(config.Kp, Is.EqualTo(50.0f), "while the PID constants are not the encoder's and stay");
        });
    }

    /// <summary>
    /// What the board refuses comes back as an error, and nothing of the refused code is recorded
    /// </summary>
    /// <remarks>
    /// Duet3Expansion ClosedLoop::ProcessM569Point1 validates everything before applying any of it, so a refusal
    /// leaves the board as it was
    /// </remarks>
    [TestCase("T2 C100 S200", "Encoder counts/rev must be at least two times steps/rev")]
    [TestCase("T2 S200", "Missing C parameter")]
    [TestCase("C1000", "M569.1 C and S parameters not permitted without T parameter")]
    [TestCase("Q0", "Torque per amp must be positive")]
    [TestCase("T5", "Invalid T value. Valid values are 1, 2 and 3")]
    public async Task M569Point1ReportsTheBoardsRefusal(string parameters, string refusal)
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point1>(refusal, CodeResult.Error))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569.1 P{DriversRig.ClosedLoopDriver(0)} {parameters}");

        DriverClosedLoopConfig config = await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.ClosedLoopBoard)!.Drivers![0].Config.ClosedLoop);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(refusal));
            Assert.That(config.EncoderType, Is.EqualTo(EncoderType.None), "nothing the board refused is recorded");
            Assert.That(config.CountsPerRev, Is.Null);
            Assert.That(config.TorqueConstant, Is.Null);
        });
    }

    /// <summary>
    /// A board built without closed loop support declines the message, which reads as RepRapFirmware's
    /// "Command is not supported" error
    /// </summary>
    /// <remarks>Duet3Expansion CommandProcessor.cpp answers m569p1 with errorNotSupported and no text when SUPPORT_CLOSED_LOOP is off</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1OnABoardWithoutClosedLoopIsNotSupported()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point1>(string.Empty, CodeResult.ErrorNotSupported))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569.1 P{DriversRig.MainDriver(0)} T2 C1000 S200");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsError(CodeErrors.NotSupported));
            Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.MainDriverBoard)!.Drivers![0].Config.ClosedLoop.EncoderType),
                        Is.EqualTo(EncoderType.None));
        });
    }

    /// <summary>
    /// M569.1 without P is refused before anything reaches the bus
    /// </summary>
    /// <remarks><c>error-missing-parameters-drivers-m569.yaml</c> (RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c>)</remarks>
    [Test]
    public async Task M569Point1WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M569.1 T2");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(CodeErrors.MissingParameter('P')));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point1>(), Is.Empty);
        });
    }

    /// <summary>
    /// Only the first driver of a list is configured
    /// </summary>
    /// <remarks>RRF GCodes3.cpp ConfigureDriver stops after the first driver for every sub-code but .3, .4 and .8</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point1ConfiguresOnlyTheFirstDriverOfAList()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync($"M569.1 P{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(3)} Q0.5");

        Assert.Multiple(() =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point1>(), Has.Count.EqualTo(1));
            (byte sentTo, CanMessageM569Point1 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point1>();
            Assert.That(sentTo, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent.P, Is.EqualTo(0));
        });
    }
}
