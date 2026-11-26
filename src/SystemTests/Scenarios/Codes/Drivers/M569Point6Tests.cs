using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.6, closed loop tuning: the board runs a manoeuvre and the code waits for its verdict
/// </summary>
/// <remarks>
/// RRF CanInterface::ConfigureRemoteDriver case 6. A board that has started a manoeuvre answers "not
/// finished" (Duet3Expansion ClosedLoop::ProcessM569Point6), and RepRapFirmware repeats the command every
/// 100ms, sending only the driver (M569Point6Params_StatusOnly), until the board answers anything else
/// </remarks>
[TestFixture]
[GCode("M569.6")]
public class M569Point6Tests : BenchFixture
{
    /// <summary>The verdicts Duet3Expansion's ClosedLoop::ProcessBasicTuningResult answers a finished basic tuning with, and how each is reported</summary>
    private static readonly object[] Verdicts =
    [
        new object[] { CodeResult.Ok, $"Driver {DriversRig.ClosedLoopDriver(0)} basic tuning succeeded, measured backlash 0.010 step", MessageType.Success },
        new object[] { CodeResult.Warning, $"Driver {DriversRig.ClosedLoopDriver(0)} basic tuning succeeded but measured backlash (0.200 step) is high", MessageType.Warning },
        new object[] { CodeResult.Error, $"Driver {DriversRig.ClosedLoopDriver(0)} basic tuning failed, measured backlash (0.500 step) is too high", MessageType.Error },
    ];

    /// <summary>
    /// Both of the board's refusals in the regression case come back as errors
    /// </summary>
    /// <remarks>
    /// <c>m569-6-closed-loop-tuning-move.yaml</c>: V1 is basic tuning, which the board refuses for an absolute
    /// encoder, and the form with no V reports the last calibration
    /// </remarks>
    [Test]
    public async Task M569Point6ReportsTheBoardsTuningVerdict()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point6>(
                message => message.V is null
                    ? $"Driver {DriversRig.ClosedLoopDriver(0)} calibration failed (no reason available)"
                    : "basic tuning is not applicable to absolute encoders",
                CodeResult.Error)).StartAsync();
        string driver = DriversRig.ClosedLoopDriver(0);

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569.6 P{driver} V1"),
                    Reply.IsError("basic tuning is not applicable to absolute encoders"));
        (_, CanMessageM569Point6 tuning) = bench.CanMaster.LastCanMessage<CanMessageM569Point6>();
        Assert.That(tuning.V, Is.EqualTo(1), "V is the manoeuvre");

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569.6 P{driver}"),
                    Reply.IsError($"Driver {driver} calibration failed (no reason available)"));
        (_, CanMessageM569Point6 status) = bench.CanMaster.LastCanMessage<CanMessageM569Point6>();
        Assert.That(status.Generic.ParamMap, Is.EqualTo(1u), "and the bare form carries the driver alone");
    }

    /// <summary>
    /// A manoeuvre the board has started is waited for: the code asks for its status, without the manoeuvre,
    /// until the board has a verdict, and that verdict is the code's reply
    /// </summary>
    [TestCaseSource(nameof(Verdicts))]
    [Category("KnownGap")]
    public async Task M569Point6WaitsForTheManoeuvreToFinish(CodeResult verdict, string text, MessageType severity)
    {
        const int unfinishedAnswers = 3;
        int answers = 0;
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.OnCanMessage((ushort)CanMessageType.M569P6,
                (fake, header, _) =>
                {
                    bool finished = Interlocked.Increment(ref answers) > unfinishedAnswers;
                    fake.InjectStandardReply(header, finished ? verdict : CodeResult.NotFinished, finished ? text : string.Empty);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569.6 P{DriversRig.ClosedLoopDriver(0)} V1");

        IReadOnlyList<(byte Board, CanMessageM569Point6 Message)> sent = bench.CanMaster.CanMessages<CanMessageM569Point6>();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.Is(severity, text), "the board's verdict, at its severity, is the reply");
            Assert.That(sent, Has.Count.EqualTo(unfinishedAnswers + 1), "the board is asked until it has a verdict, and no more");
            Assert.That(sent[0].Message.V, Is.EqualTo(1), "the first request starts the manoeuvre");
            Assert.That(sent.Skip(1).Select(request => request.Message.Generic.ParamMap), Is.All.EqualTo(1u),
                        "and the rest ask for its status with the driver alone, so the manoeuvre is not started again");
            Assert.That(sent.Select(request => (request.Board, request.Message.P)), Is.All.EqualTo((DriversRig.ClosedLoopBoard, (byte?)0)));
        });
    }

    /// <summary>
    /// The step manoeuvre's speed and acceleration travel with the manoeuvre
    /// </summary>
    /// <remarks>CANlib M569Point6Params: S and A, because V is taken by the manoeuvre</remarks>
    [Test]
    public async Task M569Point6CarriesTheStepManoeuvresSpeedAndAcceleration()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync($"M569.6 P{DriversRig.ClosedLoopDriver(0)} V64 S10 A1000");

        (_, CanMessageM569Point6 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point6>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.V, Is.EqualTo(64));
            Assert.That(sent.S, Is.EqualTo(10.0f), "S is the speed in full steps per second");
            Assert.That(sent.A, Is.EqualTo(1000.0f), "A is the acceleration in full steps per second squared");
        });
    }

    /// <summary>
    /// M569.6 without P is refused before anything reaches the bus
    /// </summary>
    /// <remarks>RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c></remarks>
    [Test]
    public async Task M569Point6WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That(await bench.Host.ExecuteCodeAsync("M569.6 V1"), Reply.IsError(CodeErrors.MissingParameter('P')));
        Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point6>(), Is.Empty);
    }
}
