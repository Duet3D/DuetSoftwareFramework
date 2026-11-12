using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Rrf;
using NUnit.Framework;

namespace EmulationTests.Rrf;

/// <summary>
/// An MB6HC running RepRapFirmware standalone with no expansion boards
/// </summary>
[TestFixture]
[Category("Emulation")]
[Category(nameof(Stack.Rrf))]
public class MainBoardTests
{
    private RrfBench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await RrfBench.StartAsync(new Dictionary<int, BoardType>(), string.Empty, Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [Test]
    public async Task M115ReportsTheFirmware()
    {
        CodeReply reply = await _bench.SendCodeAsync("M115");

        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success));
        Assert.That(reply.Text, Does.Contain("FIRMWARE_NAME: RepRapFirmware").And.Contain("ELECTRONICS: Duet 3 MB6HC"));
    }

    [Test]
    public async Task AnUnknownCodeIsAWarning()
    {
        CodeReply reply = await _bench.SendCodeAsync("M4321");

        Assert.That(reply.Severity, Is.EqualTo(MessageType.Warning), reply.ToString());
    }

    [Test]
    public async Task TheObjectModelHasTheMainBoard()
    {
        JsonNode? boards = await _bench.GetObjectModelAsync("boards");

        Assert.That(boards?[0]?["shortName"]?.GetValue<string>(), Is.EqualTo("MB6HC"));
    }
}
