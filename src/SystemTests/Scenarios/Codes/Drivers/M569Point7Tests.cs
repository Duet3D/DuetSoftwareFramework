using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.7, the motor brake: the port the board switches with the driver's enable, the delay before the driver
/// is turned off, and the PWM voltage limit
/// </summary>
/// <remarks>
/// RRF CanInterface::ConfigureRemoteDriver case 7 checks the port is on the driver's board and repackages the
/// code; Duet3Expansion's Move::ProcessM569Point7 applies it. What the board took is recorded under
/// <c>boards[].drivers[].config.brake</c> (rrf-differences.md section 3)
/// </remarks>
[TestFixture]
[GCode("M569.7")]
public class M569Point7Tests : BenchFixture
{
    /// <summary>
    /// The port, delay and voltage reach the board and are recorded, and a bare M569.7 P answers with the board's report
    /// </summary>
    /// <remarks>
    /// <c>m569-7-motor-brake-port.yaml</c>. The port name reaches the board with its board address stripped,
    /// because the board it is sent to is the board it is on
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point7BindsTheBrakePort()
    {
        const byte board = DriversRig.ClosedLoopBoard;
        string port = Expansion.Out(board, 0);
        string report = $"Driver {DriversRig.ClosedLoopDriver(0)} uses brake port {port} with voltage limited to 12.0 by PWM, brake delay 20ms";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point7>(
                message => message.C is null && message.V is null && message.S is null ? report : string.Empty)).StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)} C\"{port}\" S20")).Trim(), Is.Empty,
                    "the board took the binding");

        (byte sentTo, CanMessageM569Point7 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board), "the port's board is where the message goes");
            Assert.That(sent.P, Is.EqualTo(0));
            Assert.That(sent.C, Is.EqualTo("out0"), "and the name it carries is local to that board");
            Assert.That(sent.S, Is.EqualTo(20), "S is the brake delay in milliseconds");
            Assert.That(sent.V, Is.Null);
        });

        await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)} V12");
        (_, CanMessageM569Point7 limited) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.That(limited.V, Is.EqualTo(12.0f).Within(1e-3), "V limits the brake voltage by PWM");

        DriverBrake brake = await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.Brake);
        Assert.Multiple(() =>
        {
            Assert.That(brake.Port, Is.EqualTo(port), "the port is recorded as it was given");
            Assert.That(brake.Delay, Is.EqualTo(20));
            Assert.That(brake.Voltage, Is.EqualTo(12.0f));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)}")).TrimEnd(), Is.EqualTo(report),
                    "and a bare M569.7 P answers with the board's report");
        (_, CanMessageM569Point7 query) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.That(query.Generic.ParamMap, Is.EqualTo(1u), "which carries the driver alone");
    }

    /// <summary>
    /// A port given without S puts the delay back to the board's default
    /// </summary>
    /// <remarks>Duet3Expansion Move::ProcessM569Point7 sets the delay to DefaultDelayAfterBrakeOn whenever C is given</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point7PortWithoutDelayRestoresTheDefaultDelay()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.ClosedLoopBoard;

        await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)} C\"{Expansion.Out(board, 0)}\" S20");
        await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)} C\"{Expansion.Out(board, 1)}\"");

        DriverBrake brake = await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.Brake);
        Assert.Multiple(() =>
        {
            Assert.That(brake.Port, Is.EqualTo(Expansion.Out(board, 1)));
            Assert.That(brake.Delay, Is.Null, "the delay is the board's default again");
        });
    }

    /// <summary>
    /// C"nil" releases the port, which reaches the board as the literal name and leaves the driver without a brake
    /// </summary>
    /// <remarks>
    /// <c>m569-7-motor-brake-port.yaml</c> teardown: a port left bound to a brake is switched by every enable and
    /// disable after it, so releasing it has to work
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point7NilReleasesTheBrakePort()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.ClosedLoopBoard;

        await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)} C\"{Expansion.Out(board, 0)}\" V12");
        await bench.Host.ExecuteCodeAsync($"M569.7 P{Expansion.Driver(board, 0)} C\"nil\"");

        (_, CanMessageM569Point7 sent) = bench.CanMaster.LastCanMessage<CanMessageM569Point7>();
        Assert.That(sent.C, Is.EqualTo("nil"), "\"nil\" is a name the board reads, not an empty port");

        DriverBrake brake = await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.Brake);
        Assert.Multiple(() =>
        {
            Assert.That(brake.Port, Is.Null, "the driver has no brake");
            Assert.That(brake.Voltage, Is.EqualTo(12.0f), "while the voltage limit stays, as it does on the board");
        });
    }

    /// <summary>
    /// A port on another board is refused before anything is sent, because the driver's board is what switches it
    /// </summary>
    /// <remarks>RRF CanInterface.cpp ConfigureRemoteDriver case 7</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point7PortOnAnotherBoardIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M569.7 P{DriversRig.ClosedLoopDriver(0)} C\"{Expansion.Out(DriversRig.MainDriverBoard, 0)}\" S20");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: M569.7: Brake port must be on same board as driver"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point7>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.ClosedLoopBoard)!.Drivers![0].Config.Brake.Port),
                        Is.Null);
        });
    }

    /// <summary>
    /// What the board refuses comes back as an error, and nothing of the refused code is recorded
    /// </summary>
    /// <remarks>Duet3Expansion Move::ProcessM569Point7: a port it cannot take, and a voltage on a board without brake PWM</remarks>
    [TestCase("C\"out9\"", "Port out9 does not exist")]
    [TestCase("V12", "Brake PWM not supported by this board")]
    public async Task M569Point7ReportsTheBoardsRefusal(string parameters, string refusal)
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point7>(refusal, CodeResult.Error))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569.7 P{DriversRig.MainDriver(2)} {parameters}");

        DriverBrake brake = await bench.Host.ReadModelAsync(model => model.FindBoard(DriversRig.MainDriverBoard)!.Drivers![2].Config.Brake);
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo($"Error: M569.7: {refusal}"));
            Assert.That(brake.Port, Is.Null);
            Assert.That(brake.Voltage, Is.Null);
        });
    }

    /// <summary>
    /// M569.7 without P is refused before anything reaches the bus
    /// </summary>
    /// <remarks>RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c></remarks>
    [Test]
    public async Task M569Point7WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M569.7 S20")).TrimEnd(), Is.EqualTo("Error: M569.7: missing parameter 'P'"));
        Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point7>(), Is.Empty);
    }
}
