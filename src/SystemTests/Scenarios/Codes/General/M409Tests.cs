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

/// <summary>M409, querying the object model</summary>
[TestFixture]
[GCode("M409")]
public class M409Tests : BenchFixture
{
    /// <summary>
    /// M409 returns the queried key's live object model value as JSON, echoing the key and flags,
    /// with and without the F"v" verbose flag.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 409 answers {"key":...,"flags":...,"result":...} from
    /// ObjectModel::ReportAsJson; the result must equal what the expression evaluator reads for the
    /// same key. move.axes[0].homed is true here because config.g's G92 marked X homed
    /// </remarks>
    [Test]
    public async Task M409ReturnsTheLiveModelValue()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed);

        string reply = await bench.Host.ExecuteCodeAsync("M409 K\"move.axes[0].homed\"");
        using (JsonDocument document = JsonDocument.Parse(reply))
        {
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("key").GetString(), Is.EqualTo("move.axes[0].homed"),
                            "M409 echoes the queried key (RRF GCodes2.cpp case 409)");
                Assert.That(document.RootElement.GetProperty("result").GetRawText(), Contains.Substring(homed ? "true" : "false"),
                            "M409's result is the live value of move.axes[0].homed");
            });
        }

        string verboseReply = await bench.Host.ExecuteCodeAsync("M409 K\"move.axes[0].homed\" F\"v\"");
        using (JsonDocument document = JsonDocument.Parse(verboseReply))
        {
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("flags").GetString(), Is.EqualTo("v"),
                            "M409 echoes the F flags (RRF GCodes2.cpp case 409)");
                Assert.That(document.RootElement.GetProperty("result").GetRawText(), Contains.Substring(homed ? "true" : "false"),
                            "M409 F\"v\" still returns the live value of move.axes[0].homed");
            });
        }
    }
}
