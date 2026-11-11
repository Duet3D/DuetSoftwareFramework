using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.Link;

/// <summary>
/// The link itself, end to end: DuetControlServer and libduet_realtime_core against the fake controller.
/// Connection, configuration traffic, scripted failures, and recovery
/// </summary>
[TestFixture]
public class ConnectionTests : BenchFixture
{
    [Test]
    public async Task BootsAndKeepsExchangingAgainstTheFake()
    {
        await using Bench bench = await Bench.With().KeepingConfigTraffic().StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;

        // config.g's M953 crossed the link as a CAN enable packet
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        // The link stays alive on keep-alive exchanges
        int exchanges = fake.CompletedExchanges;
        await fake.WaitUntilAsync(() => fake.CompletedExchanges >= exchanges + 5, what: "keep-alive exchanges");

        // And the machine is responsive end to end
        string reply = await host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("FIRMWARE_NAME:"));

        Assert.That(host.Services.GetRequiredService<NativeLink>().ResyncCount, Is.Zero);
    }

    [Test]
    public async Task CorruptedCrcsAreRetriedWithoutLosingTheLink()
    {
        await using Bench bench = await Bench.With().KeepingConfigTraffic().StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        int accepts = fake.Accepts;

        fake.CorruptNextHeaderCrc();
        int exchanges = fake.CompletedExchanges;
        await fake.WaitUntilAsync(() => fake.CompletedExchanges >= exchanges + 3, what: "exchanges after a corrupt header CRC");

        // A data CRC only matters when the transfer carries data
        fake.CorruptNextDataCrc();
        fake.InjectCodeBufferUpdate(4096);
        exchanges = fake.CompletedExchanges;
        await fake.WaitUntilAsync(() => fake.CompletedExchanges >= exchanges + 3, what: "exchanges after a corrupt data CRC");

        // The retries happened below the connection: no resync, no reconnect
        Assert.That(host.Services.GetRequiredService<NativeLink>().ResyncCount, Is.Zero);
        Assert.That(fake.Accepts, Is.EqualTo(accepts));
    }

    [Test]
    [Category("LongRunning")]
    public async Task ControllerRebootReconnectsAndRunsConfigAgain()
    {
        await using Bench bench = await Bench.With().KeepingConfigTraffic().StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        fake.SimulateReboot();

        // The SBC re-dials and reconfigures the machine: a second CAN enable arrives
        await fake.WaitUntilAsync(() => fake.SbcPackets(SbcRequest.EnableCAN).Count >= 2,
                                  timeoutMs: 30_000, what: "config.g running again after the reboot");
        Assert.That(fake.Accepts, Is.GreaterThanOrEqualTo(2));

        // And the machine is responsive again
        string reply = await host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("FIRMWARE_NAME:"));
    }

    /// <summary>
    /// What each link macro records about the event it ran for, so that a scenario can assert both
    /// that it ran and what it was told
    /// </summary>
    private const string RecordDisconnect = "global wentAway = param.P\n";
    private const string RecordReconnect = "global cameBack = param.P\nglobal afterDisconnect = exists(global.wentAway)\n";

    /// <summary>What a macro recorded in a global variable, or null if it has not run</summary>
    private static JsonElement? Recorded(DuetControlServer.Model.ObjectModel model, string name)
        => model.Global.TryGetValue(name, out JsonElement? value) ? value : null;

    [Test]
    [Category("LongRunning")]
    public async Task WarmControllerResetReconnectsWithoutTheLinkDropping()
    {
        await using Bench bench = await Bench.With()
                                             .KeepingConfigTraffic()
                                             .WithSd(sd => sd.WriteSys("controller-disconnect.g", RecordDisconnect))
                                             .StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;
        int accepts = fake.Accepts;

        // A controller back before the next transfer: the restarted sequence numbers are the only
        // evidence of the outage, because nothing ever timed out
        fake.SimulateWarmReboot();

        JsonElement? cause = await host.WaitForModelAsync(model => Recorded(model, "wentAway"),
                                                          recorded => recorded is not null,
                                                          "controller-disconnect.g running");
        Assert.That(cause?.GetInt32(), Is.EqualTo(1), "the reset is what noticed the outage");

        // The machine comes back the same way it would from an outage the link did see: config.g
        // runs again and the status stops being disconnected
        await fake.WaitUntilAsync(() => fake.SbcPackets(SbcRequest.EnableCAN).Count >= 2,
                                  timeoutMs: 30_000, what: "config.g running again after the reset");
        await host.WaitForStatusAsync(MachineStatus.Idle);

        // And it recovered on the connection it already had
        Assert.That(fake.Accepts, Is.EqualTo(accepts), "the link never dropped");
    }

    [Test]
    public async Task ReconnectMacroReplacesTheDefaultRecovery()
    {
        await using Bench bench = await Bench.With().KeepingConfigTraffic().WithSd(sd =>
        {
            sd.WriteSys("controller-disconnect.g", RecordDisconnect);
            sd.WriteSys("controller-reconnect.g", RecordReconnect);
        }).StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;

        fake.SimulateReboot();

        JsonElement? recovered = await host.WaitForModelAsync(model => Recorded(model, "cameBack"),
                                                              recorded => recorded is not null,
                                                              "controller-reconnect.g running", timeoutMs: 30_000);
        (JsonElement? order, JsonElement? cause) = await host.ReadModelAsync(
            model => (Recorded(model, "afterDisconnect"), Recorded(model, "wentAway")));
        Assert.Multiple(() =>
        {
            Assert.That(recovered?.GetInt32(), Is.EqualTo(1), "the controller had reset");
            Assert.That(order?.GetBoolean(), Is.True, "the disconnect macro ran first");
            Assert.That(cause?.GetInt32(), Is.EqualTo(0), "the link timed out before the reset was seen");
        });

        // The machine that writes the macro owns the recovery, so nothing else reconfigures it
        Assert.That(fake.SbcPackets(SbcRequest.EnableCAN), Has.Count.EqualTo(1), "config.g did not run again");

        // The link being up is what ends the disconnected status, not the recovery that was chosen
        await host.WaitForStatusAsync(MachineStatus.Idle);
    }

    [Test]
    [Category("LongRunning")]
    public async Task WithheldReadinessTimesOutAndRecovers()
    {
        await using Bench bench = await Bench.With().KeepingConfigTraffic().WithSettings(new Dictionary<string, string?>
        {
            // Short enough to keep the starved stretch quick, long enough for healthy exchanges
            [nameof(DuetControlServer.Settings.SbcConnectionTimeout)] = "500",
        }).StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        fake.PauseArming();
        await Task.Delay(1500);
        fake.ResumeArming();

        int exchanges = fake.CompletedExchanges;
        await fake.WaitUntilAsync(() => fake.CompletedExchanges >= exchanges + 3,
                                  timeoutMs: 30_000, what: "exchanges after readiness was withheld");
        Assert.That(fake.Accepts, Is.GreaterThanOrEqualTo(2), "recovery re-dials the socket");

        string reply = await host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("FIRMWARE_NAME:"));
    }

    [Test]
    public async Task InjectedTrafficReachesTheDispatcher()
    {
        await using Bench bench = await Bench.With().KeepingConfigTraffic().StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster fake = bench.CanMaster;
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        // A code buffer update is the simplest firmware-to-SBC packet with no side effects to
        // configure; what this asserts is the injection path itself
        int exchanges = fake.CompletedExchanges;
        fake.InjectCodeBufferUpdate(2048);
        await fake.WaitUntilAsync(() => fake.CompletedExchanges > exchanges, what: "the prompted transfer");
        Assert.That(fake.Transfers.Where(t => t.Direction == TransferDirection.ToSbc)
                                  .SelectMany(t => t.Packets)
                                  .Any(p => p.FirmwareRequest == FirmwareRequest.CodeBufferUpdate));

        Assert.That(host.Services.GetRequiredService<NativeLink>().ResyncCount, Is.Zero);
    }
}
