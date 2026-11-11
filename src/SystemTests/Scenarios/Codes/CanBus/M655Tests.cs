using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.CanBus;

/// <summary>M655, sending a configuration string to a port on an expansion board</summary>
[TestFixture]
[GCode("M655")]
public class M655Tests : BenchFixture
{
    /// <summary>
    /// M655 needs a board to send its request to, and says so before touching the bus
    /// </summary>
    /// <remarks>
    /// M655 exists for custom CAN-connected boards with features M950 and M42 cannot express, so
    /// exactly one of B (an address) or C (a port naming one) has to say where the request goes. The
    /// regression case is <c>testcases/can/m655-custom-can-request.yaml</c>, written as its two error
    /// paths because no rig has such a board
    /// </remarks>
    [Test]
    public async Task M655WithoutABoardOrPortIsRefused()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M655 P1");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "M655 with neither B nor C is an error");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM655>(), Is.Empty,
                        "and the check happens before anything reaches the bus");
        });
    }

    /// <summary>
    /// M655 packs the parameters it was given into the message it sends the board, one of each kind
    /// </summary>
    /// <remarks>
    /// The regression case is <c>testcases/can/m655-custom-can-request.yaml</c>, whose reference
    /// records P, R and E arriving at the board, which answers "not supported by this board" because
    /// no Duet board implements a custom feature. What is being asserted is the encoding: R is
    /// signed and E is a float, so a parameter landing in the wrong field would still produce a
    /// plausible-looking message
    /// </remarks>
    [Test]
    public async Task M655SendsItsParametersToTheBoard()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M655 B{Machines.DriverBoard} P1 R-4 E0.123");

        (byte board, CanMessageM655 sent) = bench.CanMaster.LastCanMessage<CanMessageM655>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the request goes to the board B named");
            Assert.That(sent.P, Is.EqualTo(1), "P is carried as an unsigned value");
            Assert.That(sent.R, Is.EqualTo(-4), "R as a signed one, so a negative survives");
            Assert.That(sent.E, Is.EqualTo(0.123f).Within(1e-4), "and E as a float");
        });
    }

    /// <summary>
    /// M655's two string parameters: A rides in the message as written, C names the board and only
    /// the port goes with it
    /// </summary>
    /// <remarks>
    /// The regression case is <c>testcases/can/m655-string-parameters.yaml</c>. C is where the two
    /// differ: CanInterface::ProcessM655 takes the address off the front of it and sends the
    /// remainder, so <c>C"1.io0.in"</c> is a request to board 1 carrying <c>io0.in</c> - a board
    /// addresses its own ports and has no reader for an address
    /// </remarks>
    [Test]
    public async Task M655CarriesItsStringParameters()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M655 B{Machines.DriverBoard} A\"drt-string\"");
        (byte plainBoard, CanMessageM655 plain) = bench.CanMaster.LastCanMessage<CanMessageM655>();
        Assert.Multiple(() =>
        {
            Assert.That(plainBoard, Is.EqualTo(Machines.DriverBoard));
            Assert.That(plain.A, Is.EqualTo("drt-string"), "A is a plain string and travels as written");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M655 C\"{Expansion.GpioIn(Machines.DriverBoard, 0)}\"");
        (byte portBoard, CanMessageM655 port) = bench.CanMaster.LastCanMessage<CanMessageM655>();
        Assert.Multiple(() =>
        {
            Assert.That(portBoard, Is.EqualTo(Machines.DriverBoard), "C says which board the request goes to");
            Assert.That(port.C, Is.EqualTo("io0.in"),
                        "and the address comes off what it carries, as it does for every port name on the bus");
        });
    }
}
