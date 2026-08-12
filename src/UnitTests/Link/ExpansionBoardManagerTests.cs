using DuetControlServer;
using DuetControlServer.Link.Expansion;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Linq;

namespace UnitTests.Link;

/// <summary>
/// Tests for the receiver that turns expansion board status reports into object model state
/// </summary>
/// <remarks>
/// These cover the triage the link dispatch thread performs. Applying a report needs the object model
/// and its write lock, which the unit test project has no host for, so the decode-and-apply half is
/// left to integration testing against real boards
/// </remarks>
[TestFixture]
public class ExpansionBoardManagerTests
{
    private static ExpansionBoardManager NewManager() => NewManager(out _);

    private static ExpansionBoardManager NewManager(out DuetControlServer.Events.EventQueue events)
    {
        DuetControlServer.Model.ObjectModel model = new(new StoppedLifetime(),
                                                        NullLogger<DuetControlServer.Model.ObjectModel>.Instance,
                                                        Options.Create(new Settings()));
        events = new DuetControlServer.Events.EventQueue(NullLogger<DuetControlServer.Events.EventQueue>.Instance);
        return new ExpansionBoardManager(model, events, Options.Create(new Settings()),
                                         NullLogger<ExpansionBoardManager>.Instance);
    }

    /// <summary>
    /// An application lifetime that never fires, which is all the object model needs to be constructed
    /// </summary>
    private sealed class StoppedLifetime : IHostApplicationLifetime
    {
        public System.Threading.CancellationToken ApplicationStarted => System.Threading.CancellationToken.None;
        public System.Threading.CancellationToken ApplicationStopping => System.Threading.CancellationToken.None;
        public System.Threading.CancellationToken ApplicationStopped => System.Threading.CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>
    /// Every report an expansion board broadcasts on its own initiative
    /// </summary>
    private static readonly CanMessageType[] BroadcastReports =
    [
        CanMessageType.AnnounceV0,
        CanMessageType.AnnounceV1,
        CanMessageType.BoardStatusReportV0,
        CanMessageType.BoardStatusReportV1,
        CanMessageType.DriversStatusReport,
        CanMessageType.SensorTemperaturesReport,
        CanMessageType.HeatersStatusReport,
        CanMessageType.FansReport,
        CanMessageType.InputStateChangedV1,
        CanMessageType.InputStateChangedV2,
        CanMessageType.FilamentMonitorsStatusReportV2,
        CanMessageType.Event,
        CanMessageType.DebugText
    ];

    [Test]
    public void EveryBroadcastReportIsClaimed()
    {
        ExpansionBoardManager manager = NewManager();
        Assert.Multiple(() =>
        {
            foreach (CanMessageType type in BroadcastReports)
            {
                Assert.That(manager.TryEnqueue(type, 1, new byte[64]), Is.True, $"{type} should be consumed");
            }
        });
    }

    [Test]
    public void MessagesThatAreNotStatusReportsAreLeftAlone()
    {
        // Returning false is what lets the dispatcher fall through to its own handling; claiming a
        // request/response message here would swallow it
        ExpansionBoardManager manager = NewManager();
        Assert.Multiple(() =>
        {
            Assert.That(manager.TryEnqueue(CanMessageType.FirmwareBlockRequest, 1, new byte[64]), Is.False);
            Assert.That(manager.TryEnqueue(CanMessageType.StandardReply, 1, new byte[64]), Is.False);
            Assert.That(manager.TryEnqueue(CanMessageType.TimeSync, 1, new byte[64]), Is.False);
        });
    }

    [Test]
    public async System.Threading.Tasks.Task ABoardEventReachesTheEventQueue()
    {
        ExpansionBoardManager manager = NewManager(out DuetControlServer.Events.EventQueue events);
        await manager.StartAsync(System.Threading.CancellationToken.None);
        try
        {
            // The message carries the event type in its first byte, then the device number and the
            // parameter; the manager decodes it and the queue is where it ends up
            byte[] payload = new byte[64];
            payload[0] = (byte)EventType.DriverStall;
            payload[1] = 7;
            Assert.That(manager.TryEnqueue(CanMessageType.Event, 3, payload), Is.True);

            using System.Threading.CancellationTokenSource cts = new(System.TimeSpan.FromSeconds(5));
            while (events.Count == 0 && !cts.IsCancellationRequested)
            {
                await System.Threading.Tasks.Task.Delay(10, cts.Token);
            }

            Assert.That(events.TryStartProcessing(out DuetControlServer.Events.MachineEvent raised), Is.True,
                        "a board event used to be logged and dropped; it is queued now");
            Assert.That(raised!.Type, Is.EqualTo(EventType.DriverStall));
            Assert.That(raised.BoardAddress, Is.EqualTo(3), "the board it came from, which the message does not carry");
        }
        finally
        {
            await manager.StopAsync(System.Threading.CancellationToken.None);
        }
    }

    [Test]
    public void AFloodOfReportsDoesNotBlockTheCaller()
    {
        // The queue is bounded and drops the oldest, because this runs on the link dispatch thread:
        // blocking it to keep a superseded status report would stall move completions and messages
        ExpansionBoardManager manager = NewManager();
        Assert.That(Enumerable.Range(0, 5000)
                              .All(_ => manager.TryEnqueue(CanMessageType.BoardStatusReportV1, 1, new byte[64])),
                    Is.True);
    }
}
