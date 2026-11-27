using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Dsf;
using NUnit.Framework;

namespace EmulationTests.Dsf;

/// <summary>
/// An MB6HC running DuetCANMaster, driven by DuetControlServer over the emulated SBC SPI port, with no
/// expansion boards
/// </summary>
[TestFixture]
[Category("Emulation")]
[Category(nameof(Stack.Dsf))]
public class MainBoardTests
{
    private DsfBench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = (DsfBench)await Bench.StartAsync(Stack.Dsf, new Dictionary<int, BoardType>(), string.Empty, Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [Test]
    public async Task M115ReportsTheFirmware()
    {
        CodeReply reply = await _bench.SendCodeAsync("M115");

        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), reply.ToString());
        Assert.That(reply.Text, Does.Contain("Duet 3 MB6HC"));
    }

    [Test]
    public async Task TheObjectModelHasTheMainBoard()
    {
        JsonNode? board = await _bench.GetObjectModelAsync("boards[0]");

        Assert.That(board?["shortName"]?.GetValue<string>(), Is.EqualTo("MB6HC"));
        Assert.That(board?["firmwareFileName"]?.GetValue<string>(), Is.EqualTo("DuetCANMaster_MB6HC.bin"));
    }
}
