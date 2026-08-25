using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Link.Native;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// The link itself, end to end: DuetControlServer and libduet_sbc against the fake controller.
/// Connection, configuration traffic, scripted failures, and recovery
/// </summary>
[TestFixture]
public class ConnectionTests : SystemTests.Host.BenchFixture
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock");

    /// <summary>
    /// A minimal machine: the CAN bus enabled and nothing else. M953 doubles as the marker that
    /// config.g ran, because its effect must cross the link
    /// </summary>
    private const string MinimalConfig = "M953\n";

    [Test]
    public async Task BootsAndKeepsExchangingAgainstTheFake()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake, sd => sd.WriteSys("config.g", MinimalConfig));

        // config.g ran: its CAN enable arrived as a packet
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        // The link stays alive on keep-alive exchanges
        int exchanges = fake.CompletedExchanges;
        await fake.WaitUntilAsync(() => fake.CompletedExchanges >= exchanges + 5, what: "keep-alive exchanges");

        // And the machine is responsive end to end
        string reply = await host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("firmware"));

        Assert.That(host.Services.GetRequiredService<NativeLink>().ResyncCount, Is.Zero);
    }

    [Test]
    public async Task CorruptedCrcsAreRetriedWithoutLosingTheLink()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake, sd => sd.WriteSys("config.g", MinimalConfig));
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
    public async Task ControllerRebootReconnectsAndRunsConfigAgain()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake, sd => sd.WriteSys("config.g", MinimalConfig));
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        fake.SimulateReboot();

        // The SBC re-dials and reconfigures the machine: a second CAN enable arrives
        await fake.WaitUntilAsync(() => fake.SbcPackets(SbcRequest.EnableCAN).Count >= 2,
                                  timeoutMs: 30_000, what: "config.g running again after the reboot");
        Assert.That(fake.Accepts, Is.GreaterThanOrEqualTo(2));

        // And the machine is responsive again
        string reply = await host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("firmware"));
    }

    [Test]
    public async Task WithheldReadinessTimesOutAndRecovers()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake,
            sd => sd.WriteSys("config.g", MinimalConfig),
            new()
            {
                // Short enough to keep the starved stretch quick, long enough for healthy exchanges
                [nameof(DuetControlServer.Settings.SbcConnectionTimeout)] = "500",
            });
        await fake.WaitForSbcPacketAsync(SbcRequest.EnableCAN);

        fake.PauseArming();
        await Task.Delay(1500);
        fake.ResumeArming();

        int exchanges = fake.CompletedExchanges;
        await fake.WaitUntilAsync(() => fake.CompletedExchanges >= exchanges + 3,
                                  timeoutMs: 30_000, what: "exchanges after readiness was withheld");
        Assert.That(fake.Accepts, Is.GreaterThanOrEqualTo(2), "recovery re-dials the socket");

        string reply = await host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("firmware"));
    }

    [Test]
    public async Task InjectedTrafficReachesTheDispatcher()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake, sd => sd.WriteSys("config.g", MinimalConfig));
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
