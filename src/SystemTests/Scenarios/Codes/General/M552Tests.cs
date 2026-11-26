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

/// <summary>M552, the network state</summary>
[TestFixture]
[GCode("M552")]
public class M552Tests : BenchFixture
{
    /// <summary>
    /// M552 says it needs DuetPiManagementPlugin, which answers it when it is running and is not here
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 552 configures and reports its own network interfaces; the SBC's belong to the plugin
    /// (rrf-differences.md section 1)
    /// </remarks>
    [TestCase("M552")]
    [TestCase("M552 S1")]
    public async Task M552IsLeftToTheManagementPlugin(string code)
    {
        await using Bench bench = await Bench.StartAsync();
        Assert.That(await bench.Host.ExecuteCodeAsync(code), Reply.IsWarning("reserved - requires DuetPiManagementPlugin"));
    }
}
