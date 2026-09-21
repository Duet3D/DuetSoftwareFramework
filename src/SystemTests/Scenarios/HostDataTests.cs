using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// The host facts that only the SBC can state: its CPU, memory and uptime, the volumes it has
/// mounted, its network interfaces, its clock and its hostname. RepRapFirmware reads the equivalents
/// off its own hardware and publishes them from <c>RepRap::Spin</c>; here the main board is a Linux
/// machine, so <see cref="PeriodicUpdateService"/> polls the same facts out of <c>/proc</c> and the
/// mount table and writes them into the object model.
/// </summary>
/// <remarks>
/// Every scenario drives the poll rather than a code, because there is no code to run: the facts
/// reach clients through the object model alone. The interval is shortened so a scenario waits ticks
/// rather than seconds
/// </remarks>
[TestFixture]
public class HostDataTests : SystemTests.Host.BenchFixture
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock");

    /// <summary>
    /// A minimal machine: the CAN bus enabled and nothing else, as none of this needs a board
    /// </summary>
    private const string MinimalConfig = "M953\n";

    /// <summary>
    /// How often the readings and the clock are polled while these scenarios run, in ms
    /// </summary>
    private const string ReadingInterval = "100";

    /// <summary>
    /// How often the interfaces and the volumes are walked while these scenarios run, in ms
    /// </summary>
    private const string SlowPollInterval = "300";

    /// <summary>Start a host that polls its host data several times a second</summary>
    /// <param name="settingsOverrides">Extra settings on top of the shortened poll intervals</param>
    private static async Task<(ScriptedCanMaster Fake, DcsTestHost Host)> StartAsync(
        Dictionary<string, string?>? settingsOverrides = null)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(DuetControlServer.Settings.ModelUpdateInterval)] = ReadingInterval,
            [nameof(DuetControlServer.Settings.HostUpdateInterval)] = SlowPollInterval
        };
        if (settingsOverrides is not null)
        {
            foreach ((string key, string? value) in settingsOverrides)
            {
                settings[key] = value;
            }
        }

        ScriptedCanMaster fake = new(SocketPath());
        fake.AckCanRequestsWithStandardReplies();
        try
        {
            DcsTestHost host = await DcsTestHost.StartAsync(fake, sd => sd.WriteSys("config.g", MinimalConfig), settings);
            return (fake, host);
        }
        catch
        {
            fake.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The SBC's own readings reach <c>sbc</c>, which is where a client looks for the load, the free
    /// memory and how long the machine has been up
    /// </summary>
    /// <remarks>
    /// The CPU temperature is not asserted: it comes from a thermal zone that a container or a
    /// desktop need not have, and the service treats a missing one as unknown rather than as an error
    /// </remarks>
    [Test]
    public async Task SbcStatsReachTheObjectModel()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            float? avgLoad = await host.WaitForModelAsync(model => model.SBC!.CPU.AvgLoad,
                                                          load => load is not null, "sbc.cpu.avgLoad is published");
            await Assert.MultipleAsync(async () =>
            {
                Assert.That(avgLoad!.Value, Is.InRange(0f, 100f), "sbc.cpu.avgLoad is a percentage read from /proc/stat");
                Assert.That(await host.ReadModelAsync(model => model.SBC!.Memory.Available), Is.GreaterThan(0),
                            "sbc.memory.available is the MemAvailable figure from /proc/meminfo, in bytes");
                Assert.That(await host.ReadModelAsync(model => model.SBC!.Uptime), Is.GreaterThan(0),
                            "sbc.uptime is how long the Linux machine has been up, from /proc/uptime");
            });
        }
    }

#if false
    // TODO the avgLoad currently captures the average since boot. This is intended according to @chrishamm

    /// <summary>
    /// <c>sbc.cpu.avgLoad</c> is the load over the last interval rather than the average since the
    /// machine booted, so a client watching it sees the machine get busy
    /// </summary>
    /// <remarks>
    /// The since-boot average cannot move far in a second on a machine that has been up for hours,
    /// which is what makes a burst of load tell the two apart. A machine already busy when the
    /// scenario starts has no headroom to show the difference, so it skips rather than failing on
    /// something that is not the object model's doing
    /// </remarks>
    [Test]
    public async Task TheCpuLoadIsLive()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            float idleLoad = (await host.WaitForModelAsync(model => model.SBC!.CPU.AvgLoad,
                                                           load => load is not null, "sbc.cpu.avgLoad is published"))!.Value;
            if (idleLoad > 50f)
            {
                Assert.Ignore($"This machine is already {idleLoad:F0}% busy, so a burst of load proves nothing");
            }

            // Threads of their own, and two cores left alone: the machine under test is this
            // process, so burning every core through the thread pool would starve the poll whose
            // readings the scenario is waiting for
            using CancellationTokenSource burn = new();
            Thread[] burners = [.. Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount - 2)).Select(_ =>
            {
                Thread thread = new(() => { while (!burn.IsCancellationRequested) { } }) { IsBackground = true };
                thread.Start();
                return thread;
            })];

            try
            {
                float busyLoad = (await host.WaitForModelAsync(model => model.SBC!.CPU.AvgLoad,
                                                               load => load > idleLoad + 20f,
                                                               "sbc.cpu.avgLoad follows the load put on the machine",
                                                               timeoutMs: 10_000))!.Value;
                Assert.That(busyLoad, Is.GreaterThan(idleLoad + 20f),
                            "the reading is taken over the last interval, not over the whole uptime");
            }
            finally
            {
                burn.Cancel();
                foreach (Thread burner in burners)
                {
                    burner.Join();
                }
            }
        }
    }
#endif

    /// <summary>
    /// The mounted file systems reach <c>volumes</c>, which is what M21, M22 and M39 report and what
    /// a path of the form <c>n:/...</c> resolves through
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's volume 0 is the SD card it boots from (MassStorage.cpp), so volume 0 here is
    /// the file system the SBC boots from, and it is mounted for as long as the machine runs
    /// </remarks>
    [Test]
    public async Task MountedVolumesReachTheObjectModel()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            await host.WaitForModelAsync(model => model.Volumes.Count, count => count > 0, "volumes are published");
            await Assert.MultipleAsync(async () =>
            {
                Assert.That(await host.ReadModelAsync(model => model.Volumes[0].Path), Is.Not.Null.And.Not.Empty,
                            "volumes[0] names the mount point it describes");
                Assert.That(await host.ReadModelAsync(model => model.Volumes[0].Mounted), Is.True,
                            "volumes[0] is mounted (MassStorage.cpp OBJECT_MODEL mounted)");
                Assert.That(await host.ReadModelAsync(model => model.Volumes[0].Capacity), Is.GreaterThan(0),
                            "volumes[0].capacity is the size of the file system in bytes");
                Assert.That(await host.ReadModelAsync(model => model.Volumes[0].FreeSpace), Is.Not.Null,
                            "volumes[0].freeSpace is published alongside the capacity");
            });
        }
    }

    /// <summary>
    /// Every network interface the SBC has reaches <c>network.interfaces</c>, so a client reports the
    /// addresses the machine is actually reachable on
    /// </summary>
    /// <remarks>
    /// The expectation is taken from the machine the test runs on rather than written out here, since
    /// a build agent, a container and a Pi all have different interfaces. The loopback interface is
    /// left out, as RepRapFirmware has no equivalent of it
    /// </remarks>
    [Test]
    public async Task NetworkInterfacesReachTheObjectModel()
    {
        int expected = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Count(iface => iface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
        if (expected == 0)
        {
            Assert.Ignore("This machine has no network interface other than loopback");
        }

        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            await host.WaitForModelAsync(model => model.Network.Interfaces.Count, count => count == expected,
                                         $"network.interfaces lists the machine's {expected} interface(s)");

            List<NetworkInterface> interfaces = await host.ReadModelAsync(model => model.Network.Interfaces.ToList());
            Assert.Multiple(() =>
            {
                foreach (NetworkInterface iface in interfaces)
                {
                    Assert.That(iface.Mac, Is.Not.Null.And.Not.Empty, "each interface reports its MAC address");
                    Assert.That(iface.Type, Is.Not.Null, "each interface says whether it is wired or WiFi");
                }
            });
        }
    }

    /// <summary>
    /// The machine clock reaches <c>state.time</c> and the machine's uptime reaches
    /// <c>state.upTime</c>, both of which a client displays and neither of which anything else writes
    /// </summary>
    /// <remarks>
    /// RepRapFirmware keeps these from its RTC and its millisecond timer (RepRap.cpp object model
    /// <c>time</c> and <c>upTime</c>). The clock this side is the SBC clock, which is why M905 has
    /// nothing of its own to set, and the uptime is this program's, because this program is the main
    /// board
    /// </remarks>
    [Test]
    public async Task TheMachineClockAndUptimeReachTheObjectModel()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            DateTime? time = await host.WaitForModelAsync(model => model.State.Time, value => value is not null,
                                                          "state.time is published");
            Assert.That(time!.Value, Is.EqualTo(DateTime.Now).Within(TimeSpan.FromMinutes(1)),
                        "state.time follows the SBC clock");

            int firstUpTime = await host.ReadModelAsync(model => model.State.UpTime * 1000 + model.State.MsUpTime);
            Assert.That(firstUpTime, Is.GreaterThan(0), "state.upTime counts from the moment this program started");
            Assert.That(await host.ReadModelAsync(model => model.State.MsUpTime), Is.InRange(0, 999),
                        "state.msUpTime is the millisecond fraction of state.upTime");

            await host.WaitForModelAsync(model => model.State.UpTime * 1000 + model.State.MsUpTime,
                                         upTime => upTime > firstUpTime, "state.upTime advances with each poll");
        }
    }

    /// <summary>
    /// A message older than <c>MaxMessageAge</c> is dropped from <c>messages</c>, so the list a
    /// client reads does not grow without bound
    /// </summary>
    [Test]
    public async Task ExpiredMessagesAreRemoved()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync(new Dictionary<string, string?>
        {
            [nameof(DuetControlServer.Settings.MaxMessageAge)] = "1"
        });
        using (fake)
        await using (host)
        {
            using (await host.Model.AccessReadWriteAsync(CancellationToken.None))
            {
                host.Model.Messages.Add(new Message(MessageType.Success, "stale") { Time = DateTime.Now.AddMinutes(-1) });
                host.Model.Messages.Add(new Message(MessageType.Success, "fresh"));
            }

            await host.WaitForModelAsync(model => model.Messages.Select(message => message.Content).ToList(),
                                         contents => !contents.Contains("stale"),
                                         "the message older than MaxMessageAge is dropped");
            Assert.That(await host.ReadModelAsync(model => model.Messages.Any(message => message.Content == "fresh")),
                        Is.True, "a message within MaxMessageAge is kept");
        }
    }

    /// <summary>
    /// The Linux hostname changing while DuetControlServer runs reaches <c>network.hostname</c> and
    /// <c>network.name</c>, so the machine a client sees is the machine the SBC now is
    /// </summary>
    /// <remarks>
    /// The name follows the hostname because M550 refuses any name the hostname does not match
    /// (MCodeHandler.HandleSetNameAsync), so a name chosen under the old hostname would be one the
    /// machine can no longer be renamed to
    /// </remarks>
    [Test]
    public async Task AHostnameChangeReachesTheObjectModel()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            Assert.That(await host.ReadModelAsync(model => model.Network.Hostname), Is.EqualTo(Environment.MachineName),
                        "network.hostname starts as the hostname the machine booted with");

            host.Services.GetRequiredService<PeriodicUpdateService>().HostnameSource = () => "renamed-machine";

            await host.WaitForModelAsync(model => model.Network.Hostname, hostname => hostname == "renamed-machine",
                                         "network.hostname follows the Linux hostname");
            Assert.That(await host.ReadModelAsync(model => model.Network.Name), Is.EqualTo("renamed-machine"),
                        "network.name follows the hostname, as M550 would only accept a name matching it");
        }
    }

    /// <summary>
    /// The walk that spawns subprocesses runs on its own slower interval, so raising the rate of the
    /// readings does not multiply the <c>ip</c> and <c>iwgetid</c> calls behind
    /// <c>network.interfaces</c> and the volume sizes
    /// </summary>
    /// <remarks>
    /// Told apart by what the poll corrects: an entry put into the model by hand survives for as long
    /// as the slow walk has not run, while the clock the fast tick writes keeps moving. The slow
    /// interval is longer than this scenario waits, so the correction it asserts against is the one
    /// that has not happened yet
    /// </remarks>
    [Test]
    public async Task TheSubprocessWalkKeepsItsOwnInterval()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync(new Dictionary<string, string?>
        {
            [nameof(DuetControlServer.Settings.HostUpdateInterval)] = "3600000"
        });
        using (fake)
        await using (host)
        {
            int volumes = await host.WaitForModelAsync(model => model.Volumes.Count, count => count > 0,
                                                       "the first poll publishes the volumes");
            using (await host.Model.AccessReadWriteAsync(CancellationToken.None))
            {
                host.Model.Volumes.Add(new Volume { Path = "/nowhere" });
            }

            // Several fast ticks, seen through the clock they write
            DateTime? time = await host.ReadModelAsync(model => model.State.Time);
            await host.WaitForModelAsync(model => model.State.Time, value => value > time,
                                         "the readings keep being polled");

            Assert.That(await host.ReadModelAsync(model => model.Volumes.Count), Is.EqualTo(volumes + 1),
                        "the volume walk has not run again, so the extra entry is still there");
        }
    }

    /// <summary>
    /// The slow walk does run once its own interval has passed, so a volume that comes or goes
    /// reaches the object model without waiting for a restart
    /// </summary>
    [Test]
    public async Task TheSubprocessWalkRunsAtItsInterval()
    {
        (ScriptedCanMaster fake, DcsTestHost host) = await StartAsync();
        using (fake)
        await using (host)
        {
            int volumes = await host.WaitForModelAsync(model => model.Volumes.Count, count => count > 0,
                                                       "the first poll publishes the volumes");
            using (await host.Model.AccessReadWriteAsync(CancellationToken.None))
            {
                host.Model.Volumes.Add(new Volume { Path = "/nowhere" });
            }

            await host.WaitForModelAsync(model => model.Volumes.Count, count => count == volumes,
                                         "the volume walk runs again and drops the entry the host does not have");
        }
    }
}
