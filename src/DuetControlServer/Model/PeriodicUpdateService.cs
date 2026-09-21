using DuetAPI.ObjectModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model;

/// <summary>
/// Publishes the facts only the Linux machine can state
/// </summary>
/// <remarks>
/// RepRapFirmware reads the equivalents off its own hardware in <c>RepRap::Spin</c>; the SBC is the
/// main board here, so its CPU, memory, uptime, mounted volumes, network interfaces, clock and
/// hostname are read from the system and written straight into the object model. The readings cost a
/// few reads from <c>/proc</c> and are taken every <see cref="Settings.ModelUpdateInterval"/>;
/// walking the interfaces and the volumes spawns a subprocess per interface and asks every mount for
/// its size, so it keeps the slower <see cref="Settings.HostUpdateInterval"/> of its own
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings of the application</param>
public partial class PeriodicUpdateService(
    ObjectModel model,
    ILogger<PeriodicUpdateService> logger,
    IOptions<Settings> settings) : BackgroundService
{
    /// <summary>
    /// Where the Linux hostname is read from on each update
    /// </summary>
    /// <remarks>
    /// Read through a delegate because a system test cannot rename the machine it runs on, and the
    /// hostname changing under a running DuetControlServer is the behaviour that has to be covered
    /// </remarks>
    internal Func<string> HostnameSource { get; set; } = () => Environment.MachineName;

    /// <summary>
    /// List of enabled protocols
    /// </summary>
    private readonly List<NetworkProtocol> _activeProtocols = [];

    /// <summary>
    /// Check if the given protocol is enabled
    /// </summary>
    /// <param name="protocol">Protocol to check</param>
    /// <returns>True if the protocol is enabled</returns>
    public bool IsProtocolEnabled(NetworkProtocol protocol)
    {
        lock (_activeProtocols)
        {
            return _activeProtocols.Contains(protocol);
        }
    }



    /// <summary>
    /// Called when a network protocol has been enabled
    /// </summary>
    /// <param name="protocol">Enabled protocol</param>
    public void ProtocolEnabled(NetworkProtocol protocol)
    {
        lock (_activeProtocols)
        {
            if (!_activeProtocols.Contains(protocol))
            {
                _activeProtocols.Add(protocol);
            }
        }
    }

    /// <summary>
    /// Called when a network protocol has been disabled
    /// </summary>
    /// <param name="protocol">Disabled protocol</param>
    public void ProtocolDisabled(NetworkProtocol protocol)
    {
        lock (_activeProtocols)
        {
            _activeProtocols.Remove(protocol);
        }
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Load plugin manifests
        if (settings.Value.PluginSupport)
        {
            foreach (string file in Directory.GetFiles(settings.Value.PluginDirectory))
            {
                if (file.EndsWith(".json"))
                {
                    try
                    {
                        await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
                        using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
                        Plugin plugin = new();
                        plugin.UpdateFromJson(manifestJson.RootElement);
                        plugin.Pid = -1;

                        // Do not add incomplete manifests to the object model, a plugin without id or
                        // name cannot be addressed properly
                        if (string.IsNullOrEmpty(plugin.Id) || string.IsNullOrEmpty(plugin.Name))
                        {
                            logger.LogError("Skipping incomplete plugin manifest {File}", Path.GetFileName(file));
                            continue;
                        }

                        using (await model.AccessReadWriteAsync(cancellationToken))
                        {
                            model.Plugins.Add(plugin.Id, plugin);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Failed to load plugin manifest {File}", Path.GetFileName(file));
                    }
                }
            }
        }

        // Start service
        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Poll the host for as long as the program runs, taking the readings every
    /// <see cref="Settings.ModelUpdateInterval"/> and the walk over the interfaces and the volumes
    /// every <see cref="Settings.HostUpdateInterval"/>
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string lastHostname = HostnameSource();
        Stopwatch upTime = Stopwatch.StartNew();
        Stopwatch sinceWalk = new();

        try
        {
            do
            {
                try
                {
                    // The readings are taken on every tick; the walk over the interfaces and the
                    // volumes is taken on its own slower one, because it spawns a subprocess per
                    // interface and asks every mount for its size. sinceWalk is not running until
                    // the first walk, so the first tick takes both
                    bool walkDue = !sinceWalk.IsRunning || sinceWalk.ElapsedMilliseconds >= settings.Value.HostUpdateInterval;
                    if (walkDue)
                    {
                        // Timed from the attempt, so a walk that fails waits its interval like any other
                        sinceWalk.Restart();
                    }

                    // Gather data outside the lock on a background thread (slow I/O, not cancellable)
                    var gatherTask = Task.Run<(SbcData Sbc, List<Volume>? Volumes, List<NetworkInterface>? Network)>(async () =>
                    {
                        var sbcData = await GatherSbcDataAsync(stoppingToken);
                        if (!walkDue)
                        {
                            return (sbcData, null, null);
                        }

                        var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                        var drives = DriveInfo.GetDrives();
                        var volumeData = GatherVolumeData(drives);
                        var networkData = await GatherNetworkDataAsync(networkInterfaces, stoppingToken);
                        return (sbcData, volumeData, networkData);
                    }, stoppingToken);

                    // Wait for gather to complete, but abort immediately on cancellation
                    var (sbcData, volumeData, networkData) = await gatherTask.WaitAsync(stoppingToken);

                    // Apply to model with a short write lock
                    using (await model.AccessReadWriteAsync(stoppingToken))
                    {
                        if (networkData is not null)
                        {
                            ApplyNetworkData(networkData);
                        }
                        ApplySbcData(sbcData);
                        if (volumeData is not null)
                        {
                            ApplyVolumeData(volumeData);
                        }
                        ApplyHostname();
                        ApplyClock(upTime);
                        CleanMessages();
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A single failed iteration must not terminate the whole service
                    logger.LogError(e, "Failed to perform periodic model update");
                }

                // Wait for next scheduled update check
                await Task.Delay(settings.Value.ModelUpdateInterval, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        // Apply the hostname the machine now has (must be called with OM write lock held)
        void ApplyHostname()
        {
            string hostname = HostnameSource();
            if (lastHostname != hostname)
            {
                logger.LogInformation("Hostname has been changed to {Hostname}", hostname);
                lastHostname = hostname;

                // The name goes with it: M550 refuses any name the hostname does not match, so a
                // name chosen under the old hostname is one the machine can no longer be renamed to
                model.Network.Hostname = hostname;
                model.Network.Name = hostname;
            }
        }
    }

    /// <summary>
    /// Publish the machine clock and how long the machine has been running (must be called with OM
    /// write lock held)
    /// </summary>
    /// <remarks>
    /// RepRapFirmware reads these off its RTC and its millisecond timer. The clock here is the SBC
    /// clock and the uptime is this program's, because this program is the main board
    /// </remarks>
    /// <param name="upTime">Time since this service started</param>
    private void ApplyClock(Stopwatch upTime)
    {
        // Truncated to the second the object model publishes it in, so that polling faster than the
        // clock is reported does not patch subscribers with a value that serialises identically
        DateTime now = DateTime.Now;
        model.State.Time = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        model.State.UpTime = (int)(upTime.ElapsedMilliseconds / 1000);
        model.State.MsUpTime = (int)(upTime.ElapsedMilliseconds % 1000);
    }

    /// <summary>
    /// Update network interfaces
    /// </summary>
    /// <returns>Whether the network key has been changed</returns>
    private async Task<List<NetworkInterface>> GatherNetworkDataAsync(System.Net.NetworkInformation.NetworkInterface[] networkInterfaces, CancellationToken cancellationToken)
    {
        List<NetworkInterface> result = [];
        foreach (System.Net.NetworkInformation.NetworkInterface iface in networkInterfaces)
        {
            if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                continue;
            }

            NetworkInterface ni = new();

            // IPv4 configuration
            IPAddress? ipAddress = null;
            try
            {
                ni.Mac = BitConverter.ToString(iface.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':');
                ipAddress = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                             where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                             select unicastAddress.Address).FirstOrDefault();
                ni.Subnet = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                             where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                             select unicastAddress.IPv4Mask).FirstOrDefault()?.ToString();
                ni.Gateway = (from gatewayAddress in iface.GetIPProperties().GatewayAddresses
                              where gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork
                              select gatewayAddress.Address).FirstOrDefault()?.ToString();
                ni.DnsServer = (from item in iface.GetIPProperties().DnsAddresses
                                where item.AddressFamily == AddressFamily.InterNetwork
                                select item).FirstOrDefault()?.ToString();
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Failed to get IPv4 configuration data");
            }

            // DHCP detection via "ip -4 addr"
            ni.ActualIP = ipAddress?.ToString();
            if (ni.ActualIP != null && File.Exists("/usr/sbin/ip"))
            {
                try
                {
                    using Process? proc = Process.Start(new ProcessStartInfo("/usr/sbin/ip", $"-4 address show dev {iface.Name}") { RedirectStandardOutput = true });
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync(cancellationToken);
                        string output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                        ni.ConfiguredIP = output.Contains("valid_lft forever") ? ni.ActualIP : "0.0.0.0";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    logger.LogDebug(e, "Failed to query DHCP info via ip utility");
                }
            }

            ni.Speed = (int?)(iface.Speed / 1000000);
            ni.State = iface.OperationalStatus switch
            {
                System.Net.NetworkInformation.OperationalStatus.Up => NetworkState.Active,
                System.Net.NetworkInformation.OperationalStatus.Down or System.Net.NetworkInformation.OperationalStatus.LowerLayerDown => NetworkState.Disabled,
                System.Net.NetworkInformation.OperationalStatus.Dormant => NetworkState.Idle,
                _ => null,
            };

            // Note that iface.NetworkInterfaceType is broken on Unix and cannot be used (.NET 5-6)
            if (iface.Name.StartsWith('w'))
            {
                ni.Type = NetworkInterfaceType.WiFi;
                // WifiCountry is maintained by plugins, not gathered here - it gets preserved in ApplyNetworkData
                try
                {
                    string wifiData = await File.ReadAllTextAsync("/proc/net/wireless", cancellationToken);
                    Regex signalRegex = new(iface.Name + @".*(-\d+)\.");
                    Match signalMatch = signalRegex.Match(wifiData);
                    if (signalMatch.Success)
                    {
                        ni.RSSI = int.Parse(signalMatch.Groups[1].Value);
                    }

                    if (File.Exists("/usr/sbin/iwgetid"))
                    {
                        using Process? process = Process.Start(new ProcessStartInfo
                        {
                            FileName = "/usr/sbin/iwgetid",
                            Arguments = $"{iface.Name} -r",
                            RedirectStandardOutput = true
                        });
                        if (process is not null)
                        {
                            string ssid = string.Empty;
                            process.OutputDataReceived += (sender, e) => ssid += e.Data;
                            process.BeginOutputReadLine();
                            await process.WaitForExitAsync(cancellationToken);
                            ni.SSID = ssid;
                        }
                        else
                        {
                            ni.SSID = string.Empty;
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ni.RSSI = null;
                    ni.SSID = string.Empty;
                    logger.LogDebug(e, "Failed to get WiFi data for interface {InterfaceName}", iface.Name);
                }
            }
            else
            {
                ni.Type = NetworkInterfaceType.Ethernet;
            }

            result.Add(ni);
        }
        return result;
    }

    /// <summary>
    /// Apply gathered network data to the object model (must be called with OM write lock held)
    /// </summary>
    private void ApplyNetworkData(List<NetworkInterface> gathered)
    {
        int index = 0;
        foreach (NetworkInterface data in gathered)
        {
            NetworkInterface networkInterface;
            if (index >= model.Network.Interfaces.Count)
            {
                networkInterface = new NetworkInterface();
                model.Network.Interfaces.Add(networkInterface);

                lock (_activeProtocols)
                {
                    foreach (NetworkProtocol protocol in _activeProtocols)
                    {
                        networkInterface.ActiveProtocols.Add(protocol);
                    }
                }
            }
            else
            {
                networkInterface = model.Network.Interfaces[index];
            }
            index++;

            string? wifiCountry = networkInterface.WifiCountry;
            networkInterface.Assign(data);
            if (networkInterface.Type == NetworkInterfaceType.WiFi)
            {
                networkInterface.WifiCountry = wifiCountry;
            }
        }

        for (int i = model.Network.Interfaces.Count; i > index; i--)
        {
            model.Network.Interfaces.RemoveAt(i - 1);
        }
    }

    [GeneratedRegex(@"^cpu\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)")]
    private static partial Regex _cpuRegex();

    [GeneratedRegex(@"^MemAvailable:\s*(\d+)( kB| KiB)", RegexOptions.IgnoreCase)]
    private static partial Regex _availableMemoryRegex();

    /// <summary>
    /// Update SBC data key
    /// </summary>
    private record SbcData(float? AvgLoad, float? CpuTemperature, long? AvailableMemory, double? Uptime);

    /// <summary>
    /// Gather SBC stats from /proc without holding any locks
    /// </summary>
    private async Task<SbcData> GatherSbcDataAsync(CancellationToken cancellationToken)
    {
        Regex cpuRegex = _cpuRegex();
        Regex availableMemoryRegex = _availableMemoryRegex();

        float? avgLoad = null;
        float? cpuTemperature = null;
        long? availableMemory = null;
        double? uptime = null;

        try
        {
            // Compute average CPU load
            // TODO this computes the average CPU load since boot. We might want to change it to the live average
            await foreach (string line in File.ReadLinesAsync("/proc/stat", cancellationToken))
            {
                Match match = cpuRegex.Match(line);
                if (match.Success)
                {
                    double total = 0;
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        total += double.Parse(match.Groups[i].Value);
                    }
                    avgLoad = (float)Math.Round(100 - 100 * double.Parse(match.Groups[4].Value) / total, 2);
                    break;
                }
            }

            // Try to get the CPU temperature
            if (File.Exists(settings.Value.CpuTemperaturePath))
            {
                cpuTemperature = float.Parse(await File.ReadAllTextAsync(settings.Value.CpuTemperaturePath, cancellationToken)) / settings.Value.CpuTemperatureDivider;
            }

            // Try to update memory stats
            if (File.Exists("/proc/meminfo"))
            {
                foreach (string line in await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken))
                {
                    Match availableMemoryMatch = availableMemoryRegex.Match(line);
                    if (availableMemoryMatch.Success)
                    {
                        long parsedAvailableMemory = long.Parse(availableMemoryMatch.Groups[1].Value);
                        availableMemory = (availableMemoryMatch.Groups.Count > 2) ? parsedAvailableMemory * 1024 : parsedAvailableMemory;
                        break;
                    }
                }
            }

            // Read uptime
            uptime = double.Parse((await File.ReadAllTextAsync("/proc/uptime", cancellationToken)).Split(' ')[0]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to gather SBC stats");
        }

        return new SbcData(avgLoad, cpuTemperature, availableMemory, uptime);
    }

    /// <summary>
    /// Apply gathered SBC data to the object model (must be called with OM write lock held)
    /// </summary>
    private void ApplySbcData(SbcData data)
    {
        model.SBC!.CPU.AvgLoad = data.AvgLoad;
        model.SBC!.CPU.Temperature = data.CpuTemperature;
        model.SBC!.Memory.Available = data.AvailableMemory;
        if (data.Uptime.HasValue)
        {
            model.SBC!.Uptime = data.Uptime.Value;
        }
    }

    /// <summary>
    /// Update volume devices
    /// </summary>
    /// <remarks>
    /// Volume 0 always represents the virtual SD card on DuetPi. The following code achieves this but it
    /// might need further adjustments to ensure this on every Linux distribution
    /// </remarks>
    /// <returns>Asynchronous task</returns>
    private static List<Volume> GatherVolumeData(DriveInfo[] drives)
    {
        // Read file labels from /dev/disk/by-label (if applicable)
        Dictionary<string, string> labelSymlinks = [];
        if (Directory.Exists("/dev/disk/by-label"))
        {
            DirectoryInfo dirInfo = new("/dev/disk/by-label");
            foreach (FileInfo file in dirInfo.GetFiles())
            {
                string? resolvedName = file.ResolveLinkTarget(true)?.Name;
                if (resolvedName is not null)
                {
                    labelSymlinks.Add(resolvedName, file.Name);
                }
            }
        }

        List<Volume> result = [];
        foreach (DriveInfo drive in drives)
        {
            long totalSize;
            try
            {
                totalSize = drive.TotalSize;
            }
            catch
            {
                totalSize = 0;
            }

            if (drive.DriveType != DriveType.Ram && totalSize > 0)
            {
                long freeSpace = 0;
                try
                {
                    freeSpace = drive.AvailableFreeSpace;
                }
                catch
                {
                    // Best effort
                }

                bool isNetwork = drive.DriveType == DriveType.Network;
                string? name = labelSymlinks.TryGetValue(Path.GetFileName(drive.RootDirectory.FullName), out string? label)
                    ? label
                    : (drive.VolumeLabel == "/" ? null : drive.VolumeLabel);

                result.Add(new Volume
                {
                    Capacity = isNetwork ? null : totalSize,
                    FreeSpace = isNetwork ? null : freeSpace,
                    Mounted = drive.IsReady,
                    Name = name,
                    PartitionSize = isNetwork ? null : totalSize,
                    Path = drive.RootDirectory.FullName
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Apply gathered volume data to the object model (must be called with OM write lock held)
    /// </summary>
    private void ApplyVolumeData(List<Volume> gathered)
    {
        int index = 0;
        foreach (Volume data in gathered)
        {
            Volume volume;
            if (index >= model.Volumes.Count)
            {
                volume = new Volume();
                model.Volumes.Add(volume);
            }
            else
            {
                volume = model.Volumes[index];
            }
            index++;

            volume.Assign(data);
        }

        for (int i = model.Volumes.Count; i > index; i--)
        {
            model.Volumes.RemoveAt(i - 1);
        }
    }

    /// <summary>
    /// Clean expired messages
    /// </summary>
    private void CleanMessages()
    {
        for (int i = model.Messages.Count - 1; i >= 0; i--)
        {
            if (DateTime.Now - model.Messages[i].Time > TimeSpan.FromSeconds(settings.Value.MaxMessageAge))
            {
                model.Messages.RemoveAt(i);
            }
        }
    }
}
