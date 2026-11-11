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

/// <summary>M503, echoing config.g</summary>
[TestFixture]
[GCode("M503")]
public class M503Tests : BenchFixture
{
    /// <summary>
    /// M503 echoes the content of config.g.
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 503 reads CONFIG_FILE into the reply verbatim</remarks>
    [Test]
    public async Task M503EchoesConfigFile()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        string reply = await bench.Host.ExecuteCodeAsync("M503");
        Assert.That(reply, Does.Contain($"M584 X{Expansion.Driver(Machines.DriverBoard, Machines.XDriver)} Y{Expansion.Driver(Machines.DriverBoard, Machines.YDriver)} E{Expansion.Driver(Machines.DriverBoard, Machines.EDriver)}"), "M503 echoes config.g's content (RRF GCodes2.cpp case 503)");
    }
}
