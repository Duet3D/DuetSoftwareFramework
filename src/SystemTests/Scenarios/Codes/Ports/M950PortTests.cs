using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
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
}
