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

/// <summary>M929, event logging</summary>
[TestFixture]
[GCode("M929")]
public class M929Tests : BenchFixture
{
    /// <summary>
    /// M929 starts event logging, reported as state.logFile and state.logLevel, and S0 stops it
    /// again.
    /// </summary>
    /// <remarks>
    /// RRF Platform::ConfigureLogging (Platform.cpp): S1..S3 map to warn, info and debug; the
    /// object model reports state.logLevel (off while inactive) and state.logFile, null when
    /// logging is not active (RepRap.cpp state table, Platform::GetLogFileName)
    /// </remarks>
    [Test]
    public async Task M929StartsAndStopsEventLogging()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M929 S3 P\"eventlog.txt\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M929 S3 starts logging (RRF Platform::ConfigureLogging)");
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.Model.State.LogLevel, Is.EqualTo(EventLogLevel.Debug),
                            "M929 S3 sets state.logLevel to debug (RRF Platform::ConfigureLogging; OM RepRap.cpp logLevel)");
                Assert.That(bench.Host.Model.State.LogFile, Is.EqualTo("eventlog.txt"),
                            "M929 P names state.logFile (RRF Platform::GetLogFileName; OM RepRap.cpp logFile)");
            });
        }

        await bench.Host.ExecuteCodeAsync("M929 S0");
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.Model.State.LogLevel, Is.EqualTo(EventLogLevel.Off),
                            "M929 S0 sets state.logLevel to off (RRF Platform::GetLogLevel while inactive)");
                Assert.That(bench.Host.Model.State.LogFile, Is.Null,
                            "M929 S0 clears state.logFile (RRF Platform::GetLogFileName returns null while inactive)");
            });
        }
    }
}
