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

/// <summary>M581 and M581.1, triggers</summary>
[TestFixture]
[GCode("M581", "M581.1")]
public class M581Tests : BenchFixture
{
    /// <summary>
    /// M581 with only a trigger number reports the trigger's configuration.
    /// </summary>
    /// <remarks>
    /// RRF TriggerItem::Configure (TriggerItem.cpp): with nothing to change it reports, and an
    /// unused trigger replies "Trigger n is not configured". RRF does not report triggers in the
    /// object model, so the report is the observable
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M581ReportsAnUnconfiguredTrigger()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M581 T2");
        Assert.That(reply.Trim(), Is.EqualTo("Trigger 2 is not configured"),
                    "M581 T2 without parameters reports the trigger (RRF TriggerItem::Configure report branch)");
    }

    /// <summary>
    /// M581.1 configures an expression trigger that runs trigger2.g when the expression becomes
    /// true, observed through a global the macro sets.
    /// </summary>
    /// <remarks>
    /// RRF TriggerItem::Configure command fraction 1 stores the expression and evaluates its
    /// initial value; GCodes::CheckTriggers fires trigger%u.g on the false-to-true transition
    /// (GCodes.cpp). RRF keeps no trigger state in the object model, so the macro's side effect is
    /// the assertion
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    [Category("LongRunning")]
    public async Task M581Dot1ExpressionTriggerRunsItsMacro()
    {
        await using Bench bench = await Bench.With("global fireTrigger = false\nglobal trigRan = 0\n")
                                             .WithSd(sd => sd.WriteSys("trigger2.g", "set global.trigRan = global.trigRan + 1\n"))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M581.1 T2 P\"global.fireTrigger\" R0");
        Assert.That(reply, Does.Not.Contain("Error"), "M581.1 accepts an expression trigger (RRF TriggerItem::Configure fraction 1)");

        await bench.Host.ExecuteCodeAsync("set global.fireTrigger = true");
        await bench.Host.WaitForGlobalAsync("trigRan", 1);
        Assert.That(await bench.Host.GlobalAsync("trigRan"), Is.EqualTo(1),
                    "the expression's false-to-true transition ran trigger2.g once (RRF GCodes::CheckTriggers)");
    }
}
