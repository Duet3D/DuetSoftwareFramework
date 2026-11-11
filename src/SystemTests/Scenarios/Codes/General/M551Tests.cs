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

/// <summary>M551, the password</summary>
[TestFixture]
[GCode("M551")]
public class M551Tests : BenchFixture
{
    /// <summary>
    /// M551 sets the password and reports nothing.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 551: RepRap::SetPassword, with no option to report it and no object
    /// model field, so the silent reply is the whole observable contract
    /// </remarks>
    [Test]
    public async Task M551AcceptsThePassword()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M551 P\"benchpass\"");
        Assert.That(reply.Trim(), Is.Empty, "M551 P sets the password without a report (RRF GCodes2.cpp case 551)");
    }
}
