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

/// <summary>M957, raising an event</summary>
[TestFixture]
[GCode("M957")]
public class M957Tests : BenchFixture
{
    /// <summary>
    /// M957 raises an event whose macro runs, observed through a global the macro sets.
    /// </summary>
    /// <remarks>
    /// RRF GCodes::RaiseEvent (GCodes3.cpp) queues the event; GCodes::ProcessEvent runs the macro
    /// named by Event::GetMacroFileName, driver-warning.g for driver_warning (Event.cpp,
    /// underscores become dashes). driver_warning's default action only logs, so the raised event
    /// leaves the machine running
    /// </remarks>
    [Test]
    public async Task M957RaisesAnEventAndRunsItsMacro()
    {
        await using Bench bench = await Bench.With("global eventRan = 0\n")
                                             .WithSd(sd => sd.WriteSys("driver-warning.g", "set global.eventRan = global.eventRan + 1\n"))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M957 E\"driver_warning\" D0 B1");
        Assert.That(reply, Does.Not.Contain("Error"), "M957 accepts a driver_warning event (RRF GCodes::RaiseEvent)");
        await bench.Host.WaitForGlobalAsync("eventRan", 1);
        Assert.That(await bench.Host.GlobalAsync("eventRan"), Is.EqualTo(1),
                    "the raised event ran driver-warning.g once (RRF GCodes::ProcessEvent, Event::GetMacroFileName)");
    }
}
