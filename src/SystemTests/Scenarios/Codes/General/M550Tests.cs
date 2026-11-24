using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M550, the machine name</summary>
[TestFixture]
[GCode("M550")]
public class M550Tests : BenchFixture
{
    /// <summary>M550 without P reports network.name</summary>
    /// <remarks>RRF GCodes2.cpp case 550 replies "RepRap name: %s" with the name network.name reports</remarks>
    [Test]
    public async Task M550ReportsTheMachineName()
    {
        await using Bench bench = await Bench.StartAsync();

        string name = await bench.Host.ReadModelAsync(model => model.Network.Name);
        Assert.That((await bench.Host.ExecuteCodeAsync("M550")).Trim(), Is.EqualTo($"RepRap name: {name}"));
    }

    /// <summary>
    /// Without DuetPiManagementPlugin, which changes the hostname, M550 P takes a name that spells the hostname and
    /// refuses any other
    /// </summary>
    /// <remarks>RRF takes any name (reprap.SetName); rrf-differences.md section 1</remarks>
    [Test]
    public async Task M550TakesANameThatSpellsTheHostname()
    {
        await using Bench bench = await Bench.StartAsync();

        string spaced = Environment.MachineName.Insert(1, " ");
        string accepted = await bench.Host.ExecuteCodeAsync($"M550 P\"{spaced}\"");
        Assert.Multiple(async () =>
        {
            Assert.That(accepted.Trim(), Is.Empty, "only letters and digits are compared");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Network.Name), Is.EqualTo(spaced), "network.name is the name as given");
        });

        string refused = await bench.Host.ExecuteCodeAsync($"M550 P\"{Environment.MachineName}2\"");
        Assert.Multiple(async () =>
        {
            Assert.That(refused, Does.StartWith("Error:").And.Contain("hostname"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Network.Name), Is.EqualTo(spaced), "a refused name changes nothing");
        });
    }
}
