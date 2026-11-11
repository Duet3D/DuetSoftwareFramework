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
    /// <summary>
    /// M550 sets the machine name, which the object model reports as network.name, and reports it
    /// in RepRapFirmware's wording.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 550: P sets the name via reprap.SetName, no P replies
    /// "RepRap name: %s". The name is reported as network.name (Network.cpp objectModelTable,
    /// reprap.GetName()); RRF has no state.machineName
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M550SetsTheMachineName()
    {
        await using Bench bench = await Bench.StartAsync();
        string omName = await bench.Host.ReadModelAsync(model => model.Network.Name);
        Assert.That((await bench.Host.ExecuteCodeAsync("M550")).Trim(), Is.EqualTo($"RepRap name: {omName}"),
                    "M550 without P reports network.name (RRF GCodes2.cpp case 550)");

        string reply = await bench.Host.ExecuteCodeAsync("M550 P\"Bench550\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M550 P accepts a new machine name (RRF reprap.SetName)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Network.Name), Is.EqualTo("Bench550"),
                    "M550 P sets network.name (RRF Network.cpp name entry)");
    }
}
