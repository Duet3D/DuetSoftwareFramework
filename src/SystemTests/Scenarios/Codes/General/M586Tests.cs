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

/// <summary>M586 and M586.4, the CORS site and the MQTT client</summary>
[TestFixture]
[GCode("M586", "M586.4")]
public class M586Tests : BenchFixture
{
    /// <summary>
    /// M586 reports and sets the CORS site, which the object model carries as network.corsSite.
    /// </summary>
    /// <remarks>
    /// RRF Network::ConfigureNetworkProtocol (Network.cpp): C sets the site via SetCorsSite, and
    /// the report is "CORS enabled for site '%s'" or "CORS disabled". The site is network.corsSite
    /// (Network.cpp objectModelTable)
    /// </remarks>
    [Test]
    public async Task M586ConfiguresTheCorsSite()
    {
        await using Bench bench = await Bench.StartAsync();
        Assert.That(await bench.Host.ExecuteCodeAsync("M586"), Does.Contain("CORS disabled"),
                    "M586 without C reports no CORS site (RRF Network::ConfigureNetworkProtocol)");

        await bench.Host.ExecuteCodeAsync("M586 C\"example.com\"");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Network.CorsSite), Is.EqualTo("example.com"),
                    "M586 C sets network.corsSite (RRF Network::SetCorsSite; OM Network.cpp corsSite)");
        Assert.That(await bench.Host.ExecuteCodeAsync("M586"), Does.Contain("CORS enabled for site 'example.com'"),
                    "M586's report names the configured site (RRF Network::ConfigureNetworkProtocol)");

        await bench.Host.ExecuteCodeAsync("M586 C\"\"");
        Assert.That(await bench.Host.ExecuteCodeAsync("M586"), Does.Contain("CORS disabled"),
                    "M586 C\"\" clears the CORS site (RRF Network::SetCorsSite with an empty string)");
    }

    /// <summary>
    /// M586.4 configures the MQTT client silently.
    /// </summary>
    /// <remarks>
    /// RRF Network::ConfigureNetworkProtocol dispatches command fraction 4 (MqttProtocol,
    /// NetworkDefs.h) to MqttClient::Configure, which accepts client id, username and password
    /// while the client is disabled and replies nothing. MQTT settings have no object model
    /// fields in RRF
    /// </remarks>
    [Test]
    public async Task M586Dot4ConfiguresTheMqttClient()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M586.4 C\"benchclient\" U\"user\" K\"pass\"");
        Assert.That(reply.Trim(), Is.Empty, "M586.4 configures the MQTT client without a report (RRF MqttClient::Configure)");
    }
}
