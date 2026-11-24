using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Ports;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Fans.FanSetup;

namespace SystemTests.Scenarios.Codes.Ports;

/// <summary>
/// M950 P and S, creating general purpose outputs and servos, and the device letter every M950 needs exactly
/// one of
/// </summary>
[TestFixture]
[GCode("M950")]
public class M950PortTests : BenchFixture
{
    /// <summary>
    /// M950 says which device it creates by the letter it carries, so a code with none of them and a
    /// code with two are both refused, and neither creates anything or reaches a board
    /// </summary>
    /// <remarks>
    /// Platform::ConfigurePort (Platform.cpp) counts the device letters and takes anything but
    /// exactly one as a malformed line. The regression case is
    /// <c>testcases/fans/error-missing-parameters-fans.yaml</c>, whose reference records both
    /// rejections with an empty object-model delta and no CAN traffic: a guard that stopped firing
    /// would mean a malformed config.g line being acted on rather than refused
    /// </remarks>
    [Test]
    public async Task M950WithoutExactlyOneDeviceLetterIsRefused()
    {
        await using Bench bench = await Bench.StartAsync();

        string none = await bench.Host.ExecuteCodeAsync("M950");
        string two = await bench.Host.ExecuteCodeAsync("M950 F0 P0");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(none, Does.StartWith("Error:"),
                        "M950 naming no device is an error (Platform.cpp ConfigurePort)");
            Assert.That(two, Does.StartWith("Error:"),
                        "M950 naming two devices is the same rejection as naming none (Platform.cpp ConfigurePort)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans.Count), Is.Zero,
                        "neither code added anything to fans[]");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut.Count), Is.Zero,
                        "nor to state.gpOut[]");
        });

        Assert.Multiple(() =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Fan>(), Is.Empty,
                        "and no board was told to claim a fan port");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Gpio>(), Is.Empty,
                        "nor an output port");
        });
    }

    /// <summary>
    /// M950 P creates a general-purpose output at the default M42 frequency, off, with its port
    /// recorded
    /// </summary>
    /// <remarks>
    /// GpOutputPort::Configure (GpOutPort.cpp): with C and no Q the frequency is
    /// DefaultPinWritePwmFreq = 500 Hz for the non-servo form (RRF3Common.h). The OM table
    /// (GpOutPort.cpp) reports freq and pwm
    /// </remarks>
    [Test]
    public async Task M950CreatesGpOutPortWithDefaultFrequency()
    {
        await using Bench bench = await Bench.With($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"")
                                             .KeepingConfigTraffic()
                                             .StartAsync();

        GpOutputPort? gpOutPort = await bench.Host.ReadModelAsync(model => model.State.GpOut[0]);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(gpOutPort!.Freq, Is.EqualTo(500),
                        "M950 P0 without Q defaults state.gpOut[0].freq to DefaultPinWritePwmFreq 500 (GpOutPort.cpp Configure)");
            Assert.That(gpOutPort!.Pwm, Is.Zero,
                        "a new output starts with state.gpOut[0].pwm 0");
            Assert.That(gpOutPort!.Port, Is.EqualTo($"{Expansion.Out(Machines.DriverBoard, 4)}"),
                        "M950 P0 C records state.gpOut[0].port (DSF addition, rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Gpio sent) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sent.P, Is.EqualTo(0), "P is the port number the board will know it by");
            Assert.That(sent.C, Is.EqualTo("out4"), "C is the pin as the board knows it, address taken off");
            Assert.That(sent.Q, Is.EqualTo(500),
                        "Q states DefaultPinWritePwmFreq rather than leaving the board to default it "
                        + "(GpOutPort.cpp Configure resolves the frequency before it sends anything)");
            Assert.That(sent.S, Is.Zero, "S is the servo flag, and this is a plain output");
        });
    }

    /// <summary>M950 P with Q and no C changes only the frequency of an existing output</summary>
    /// <remarks>GpOutputPort::Configure (GpOutPort.cpp), the frequency-only form</remarks>
    [Test]
    public async Task M950SetsGpOutFrequencyWithoutPort()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");

        await bench.Host.ExecuteCodeAsync("M950 P0 Q100");
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Freq), Is.EqualTo(100),
                    "M950 P0 Q100 with no C changes state.gpOut[0].freq (GpOutPort.cpp Configure, frequency-only form)");

        (byte board, CanMessageM950Gpio sent) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sent.P, Is.EqualTo(0), "P names the port being changed");
            Assert.That(sent.Q, Is.EqualTo(100), "Q is the new frequency");
            Assert.That(sent.C, Is.Null,
                        "and no C or S: the frequency-only branch sends those two alone, so the board's port "
                        + "assignment survives (GpOutPort.cpp Configure)");
            Assert.That(sent.S, Is.Null);
        });
    }

    /// <summary>M950 S creates a servo at the 50 Hz refresh frequency</summary>
    /// <remarks>
    /// GpOutputPort::Configure (GpOutPort.cpp): the servo form without Q uses
    /// DefaultServoRefreshFrequency = 50 Hz (RRF3Common.h)
    /// </remarks>
    [Test]
    public async Task M950CreatesServoAtRefreshFrequency()
    {
        await using Bench bench = await Bench.With($"M950 S0 C\"{Expansion.Out(Machines.DriverBoard, 5)}\"")
                                             .KeepingConfigTraffic()
                                             .StartAsync();

        Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Freq), Is.EqualTo(50),
                    "M950 S0 without Q defaults state.gpOut[0].freq to DefaultServoRefreshFrequency 50 (GpOutPort.cpp Configure)");

        (byte board, CanMessageM950Gpio sent) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sent.S, Is.EqualTo(1),
                        "S reaches the board as the servo flag, which is the whole difference between the two "
                        + "forms of M950 (Platform.cpp ConfigurePort routes both to gpoutPorts[])");
            Assert.That(sent.P, Is.EqualTo(0),
                        "and the servo number travels as P: the board reads P as the port number, so the number "
                        + "in an M950 S cannot be passed through as written");
            Assert.That(sent.Q, Is.EqualTo(50), "Q states DefaultServoRefreshFrequency");
            Assert.That(sent.C, Is.EqualTo("out5"), "with the address taken off, as every port name on the bus is");
        });
    }

    /// <summary>
    /// M950 D configures the MB6HC's second SD slot, which RepRapFirmware refuses on an SBC
    /// </summary>
    /// <remarks>
    /// Platform::ConfigurePort answers D with <c>SD card attached to Duet is not supported in SBC mode</c>
    /// when it runs with an SBC, which DSF always is. The regression reference m950-sd-slot was
    /// recorded on a standalone board, where the slot is created
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950DIsRefusedOnAnSbc()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M950 D1 C\"{Expansion.SpiCs(Machines.DriverBoard, 0)}\"");
        Assert.That(reply, Does.StartWith("Error:").And.Contain("not supported in SBC mode"));
    }

    /// <summary>
    /// M950 P C"nil" releases the output's pin on its board and empties state.gpOut[]
    /// </summary>
    /// <remarks>
    /// GpOutputPort::Configure sends P and C"nil" to the board that held the port. The regression
    /// reference m950-gpio-output records <c>{C: "nil", P: 0}</c> answered with success and nothing else
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950DeletesAnOutputWithCNil()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\" Q500");

        string reply = await bench.Host.ExecuteCodeAsync("M950 P0 C\"nil\"");
        var sent = bench.CanMaster.CanMessages<CanMessageM950Gpio>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty, "the delete succeeds");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut.ElementAtOrDefault(0)), Is.Null,
                        "state.gpOut[0] is empty");
            Assert.That(sent, Has.Count.EqualTo(1), "one message, the release");
            Assert.That(sent[0].Board, Is.EqualTo(Machines.DriverBoard), "to the board that held the pin");
            Assert.That(sent[0].Message.P, Is.EqualTo(0));
            Assert.That(sent[0].Message.C, Is.EqualTo("nil"));
            Assert.That(sent[0].Message.Q, Is.Null, "and nothing else");
            Assert.That(sent[0].Message.S, Is.Null);
        });
    }

    /// <summary>
    /// M950 P with a new port releases the old pin before it claims the new one
    /// </summary>
    /// <remarks>GpOutputPort::Configure, "Remove any existing assignment"</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950MovesAnOutputReleasingTheOldPinFirst()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");

        await bench.Host.ExecuteCodeAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 5)}\"");
        var sent = bench.CanMaster.CanMessages<CanMessageM950Gpio>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(sent.Select(m => m.Message.C), Is.EqualTo(new[] { "nil", "out5" }),
                        "the old pin is released before the new one is claimed");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Port),
                        Is.EqualTo(Expansion.Out(Machines.DriverBoard, 5)), "state.gpOut[0].port is the new port");
        });
    }

    /// <summary>
    /// A pin that is already in use is refused by its board, and the refused output is not left in
    /// state.gpOut[]
    /// </summary>
    /// <remarks>
    /// The regression reference m950-error-port-in-use records the board's <c>Pin 3.out6 is not free</c>
    /// as the code's error. RepRapFirmware then reports <c>state.gpOut[5]</c> with freq 500 and pwm 0
    /// anyway, because GpOutputPort::Configure sets the board address before it sends; here the output
    /// is not recorded (rrf-differences.md)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task APortThatIsInUseIsRefusedAndNotRecorded()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P4 C\"{Expansion.Out(Machines.DriverBoard, 6)}\"");
        bench.CanMaster.AnswerWith<CanMessageM950Gpio>(message => message.P == 5 && message.C == "out6"
            ? (CodeResult.Error, "Pin 1.out6 is not free")
            : (CodeResult.Ok, string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync($"M950 P5 C\"{Expansion.Out(Machines.DriverBoard, 6)}\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("Pin 1.out6 is not free"),
                        "the board's refusal is the result");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut.ElementAtOrDefault(5)), Is.Null,
                        "and state.gpOut[5] is empty");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[4]?.Port),
                        Is.EqualTo(Expansion.Out(Machines.DriverBoard, 6)), "while output 4 keeps the pin");
        });
    }

    /// <summary>
    /// A board that will not release the old pin is a warning, and the new port is still claimed
    /// </summary>
    /// <remarks>GpOutputPort::Configure reports the failed release as a warning message and carries on</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task AFailedReleaseIsAWarning()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");
        bench.CanMaster.AnswerWith<CanMessageM950Gpio>(message => message.C == "nil"
            ? (CodeResult.Error, "Port 0 not found")
            : (CodeResult.Ok, string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 5)}\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Warning:").And.Contain("Port 0 not found"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Port),
                        Is.EqualTo(Expansion.Out(Machines.DriverBoard, 5)), "and the new port is still claimed");
        });
    }

    /// <summary>
    /// M950 P with no C or Q asks the output's board to report it and answers with what it says
    /// </summary>
    /// <remarks>
    /// GpOutputPort::Configure sends P alone. The regression reference m950-report-port records the
    /// board's <c>GPIO/servo port 3 pin 3.out5 frequency 500Hz</c> as the reply
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950AsksTheBoardToReportAnOutput()
    {
        const string report = "GPIO/servo port 3 pin 1.out5 frequency 500Hz";
        await using Bench bench = await Bench.StartAsync($"M950 P3 C\"{Expansion.Out(Machines.DriverBoard, 5)}\" Q500");
        bench.CanMaster.ReportWith<CanMessageM950Gpio>(report);

        string reply = await bench.Host.ExecuteCodeAsync("M950 P3");
        (byte board, CanMessageM950Gpio query) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.EqualTo(report), "the board's report is the reply");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(query.P, Is.EqualTo(3), "the query names the port");
            Assert.That(query.C, Is.Null, "and nothing else");
            Assert.That(query.Q, Is.Null);
            Assert.That(query.S, Is.Null);
        });
    }

    /// <summary>
    /// An output with no pin is reported as having none, without asking any board
    /// </summary>
    /// <remarks>GpOutputPort::Configure, the local branch: <c>GPIO/servo port 7</c> and IoPort's <c>has no pin</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task AnOutputWithNoPinIsReportedAsSuch()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M950 P7");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.EqualTo("GPIO/servo port 7 has no pin"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Gpio>(), Is.Empty);
        });
    }

    /// <summary>
    /// M950 P Q for an output with no pin is accepted and changes nothing
    /// </summary>
    /// <remarks>GpOutputPort::Configure, the frequency-only form on a local port, which answers ok</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950QForAnOutputWithNoPinChangesNothing()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M950 P7 Q100");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut.Count), Is.Zero);
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Gpio>(), Is.Empty);
        });
    }

    /// <summary>
    /// A frequency change the board refuses is not recorded
    /// </summary>
    /// <remarks>GpOutputPort::Configure sets the frequency only when the board's reply Succeeded</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ARefusedOutputFrequencyChangeIsNotRecorded()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");
        bench.CanMaster.ReportWith<CanMessageM950Gpio>("Frequency out of range", CodeResult.Error);

        string reply = await bench.Host.ExecuteCodeAsync("M950 P0 Q100");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Freq), Is.EqualTo(500),
                        "state.gpOut[0].freq keeps what the board still has");
        });
    }

    /// <summary>
    /// An output or servo number past MaxGpOutPorts is refused before the port is looked at
    /// </summary>
    /// <remarks>Platform::ConfigurePort: <c>GetLimitedUIValue('P', MaxGpOutPorts)</c>, and the same for S</remarks>
    [TestCase('P')]
    [TestCase('S')]
    public async Task M950RefusesAnOutputNumberPastTheLimit(char letter)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M950 {letter}{GpioManager.MaxGpOutPorts} C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Gpio>(), Is.Empty);
        });
    }

    /// <summary>
    /// state.gpOut[].servo says whether the port was created by M950 S or by M950 P, which the board
    /// drives differently and which is needed to create it again
    /// </summary>
    [Test]
    [Category("KnownGap")]
    public async Task TheObjectModelRecordsWhetherAnOutputIsAServo()
    {
        await using Bench bench = await Bench.StartAsync(
            $"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"\nM950 S1 C\"{Expansion.GpioOut(Machines.DriverBoard, 4)}\"");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Servo), Is.False);
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[1]!.Servo), Is.True);
        });
    }
}
