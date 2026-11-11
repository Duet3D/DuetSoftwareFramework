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
    /// M552 without parameters reports the network state.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 552 with no S or P calls Network::GetNetworkState, which replies
    /// "Network is %s, configured IP address: %s, actual IP address: %s" for an ethernet
    /// interface (for example W5500Interface::GetNetworkState). The interfaces themselves are
    /// network.interfaces[] (Network.cpp objectModelTable); rrf-differences.md documents no
    /// deviation for M552
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M552ReportsTheNetworkState()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M552");
        Assert.That(reply, Does.Match("Network is (enabled|disabled)"),
                    "M552 without parameters reports the network state (RRF Network::GetNetworkState)");
    }
}
