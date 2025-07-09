using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Model;

/// <summary>
/// Static class that updates the machine model in certain intervals
/// </summary>
/// <param name="codeFactory">Code factory to create codes</param>
/// <param name="model">Object model</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings of the application</param>
public partial class PeriodicUpdateService(CodeFactory codeFactory, ObjectModel model, ILogger<PeriodicUpdateService> logger, IOptions<Settings> settings) : BackgroundService
{
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

    /// <summary>
    /// Start the periodic update service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
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
                        plugin.UpdateFromJson(manifestJson.RootElement, false);
                        plugin.Pid = -1;
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
    /// Run model updates in a certain interval.
    /// This function updates host properties like network interfaces and storage devices
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan measuredDelay = TimeSpan.Zero;
        string lastHostname = Environment.MachineName;
        bool updateNetworkSeq, updateVolumesSeq;
        string? lastIPAddress = null;

        do
        {
            // Prefetch the network and volume devices because this can take quite a while (> 1.5s)
            System.Net.NetworkInformation.NetworkInterface[] networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            DriveInfo[] drives = DriveInfo.GetDrives();

            // Run another update cycle
            string currentIPAddress;
            using (await model.AccessReadWriteAsync(stoppingToken))
            {
                updateNetworkSeq = await UpdateNetworkAsync(networkInterfaces, stoppingToken);
                currentIPAddress = model.Network.Interfaces.FirstOrDefault(iface => iface.ActualIP != null)?.ActualIP ?? "0.0.0.0";
                UpdateSbc();
                updateVolumesSeq = UpdateVolumes(drives);
                CleanMessages();
            }

            // Check if the system time has to be updated
            if (measuredDelay > TimeSpan.FromMilliseconds(settings.Value.HostUpdateInterval + 2000) && !Debugger.IsAttached)
            {
                logger.LogInformation("System time has been changed");
                Code code = codeFactory.Create();
                code.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                code.Channel = CodeChannel.Trigger;
                code.Type = CodeType.MCode;
                code.MajorNumber = 905;
                code.Parameters =
                [
                    new('P', DateTime.Now.ToString("yyyy-MM-dd")),
                    new('S', DateTime.Now.ToString("HH:mm:ss"))
                ];
                await code.ExecuteAsync(stoppingToken);
            }

            // Check if the hostname has to be updated
            if (lastHostname != Environment.MachineName)
            {
                logger.LogInformation("Hostname has been changed");
                lastHostname = Environment.MachineName;
                Code code = codeFactory.Create();
                code.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                code.Channel = CodeChannel.Trigger;
                code.Type = CodeType.MCode;
                code.MajorNumber = 550;
                code.Parameters =
                [
                    new('P', lastHostname)
                ];
                await code.ExecuteAsync(stoppingToken);
            }

            // Check if the network key has been updated
            if (updateNetworkSeq)
            {
                // Update the network seq value
                Code code = codeFactory.Create();
                code.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                code.Channel = CodeChannel.Trigger;
                code.Type = CodeType.MCode;
                code.MajorNumber = 409;
                code.Parameters =
                [
                    new('K', "network"),
                    new('I', 1)
                ];
                await code.ExecuteAsync(stoppingToken);

                // Update the IP address to report on 12864 displays
                if (currentIPAddress != lastIPAddress)
                {
                    lastIPAddress = currentIPAddress;

                    code = codeFactory.Create();
                    code.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                    code.Channel = CodeChannel.Trigger;
                    code.Type = CodeType.MCode;
                    code.MajorNumber = 552;
                    code.Parameters =
                    [
                        new('P', currentIPAddress ?? "0.0.0.0")
                    ];
                    await code.ExecuteAsync(stoppingToken);
                }
            }

            if (updateVolumesSeq)
            {
                Code code = codeFactory.Create();
                code.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                code.Channel = CodeChannel.Trigger;
                code.Type = CodeType.MCode;
                code.MajorNumber = 409;
                code.Parameters =
                [
                    new('K', "volumes"),
                    new('I', 1)
                ];
                await code.ExecuteAsync(stoppingToken);
            }

            // Wait for next scheduled update check
            DateTime lastUpdateTime = DateTime.Now;
            await Task.Delay(settings.Value.HostUpdateInterval, stoppingToken);
            measuredDelay = DateTime.Now - lastUpdateTime;
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    /// <summary>
    /// Update network interfaces
    /// </summary>
    /// <returns>Whether the network key has been changed</returns>
    private async Task<bool> UpdateNetworkAsync(System.Net.NetworkInformation.NetworkInterface[] networkInterfaces, CancellationToken cancellationToken = default)
    {
        bool networkUpdated = false;
        void InterfaceUpdated(object? sender, PropertyChangedEventArgs e) => networkUpdated = true;

        // DCS does not maintain the WiFi country code, so we need to cache it if was populated before
        string? wifiCountry = model.Network.Interfaces.FirstOrDefault(iface => iface.WifiCountry != null)?.WifiCountry;

        int index = 0;
        foreach (System.Net.NetworkInformation.NetworkInterface iface in networkInterfaces)
        {
            if (iface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
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
                networkInterface.PropertyChanged += InterfaceUpdated;

                // Update IPv4 configuration
                string? macAddress = null;
                IPAddress? ipAddress = null, netMask = null, gateway = null, dnsServer = null;
                try
                {
                    macAddress = BitConverter.ToString(iface.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':');
                    ipAddress = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                    where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                    select unicastAddress.Address).FirstOrDefault();
                    netMask = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                select unicastAddress.IPv4Mask).FirstOrDefault();
                    gateway = (from gatewayAddress in iface.GetIPProperties().GatewayAddresses
                                where gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                select gatewayAddress.Address).FirstOrDefault();
                    dnsServer = (from item in iface.GetIPProperties().DnsAddresses
                                    where item.AddressFamily == AddressFamily.InterNetwork
                                    select item).FirstOrDefault();
                }
                catch (Exception e)
                {
                    logger.LogDebug(e, "Failed to get IPv4 configuration data");
                }

                // .NET cannot determine if DHCP is used for a given adapter on Linux, so use "ip -4 addr" to get the IPv4 address lifetime (if any)
                string? ipAddr = ipAddress?.ToString();
                if (ipAddr != null)
                {
                    if (File.Exists("/usr/sbin/ip"))
                    {
                        try
                        {
                            using Process? proc = Process.Start(new ProcessStartInfo("/usr/sbin/ip", $"-4 address show dev {iface.Name}") { RedirectStandardOutput = true });
                            if (proc != null)
                            {
                                await proc.WaitForExitAsync(cancellationToken);

                                // Static IPv4 addresses do not have limited lifetimes
                                string output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                                networkInterface.ConfiguredIP = output.Contains("valid_lft forever") ? ipAddr : "0.0.0.0";
                            }
                        }
                        catch (Exception e)
                        {
                            logger.LogDebug(e, "Failed to query DHCP info via ip utility");
                        }
                    }
                }
                else
                {
                    networkInterface.ConfiguredIP = null;
                }

                // Assign other IPv4 properties
                networkInterface.ActualIP = ipAddr;
                networkInterface.Subnet = netMask?.ToString();
                networkInterface.Gateway = gateway?.ToString();
                networkInterface.DnsServer = dnsServer?.ToString();
                networkInterface.Mac = macAddress;
                networkInterface.Speed = (int?)(iface.Speed / 1000000);
                networkInterface.State = iface.OperationalStatus switch
                {
                    System.Net.NetworkInformation.OperationalStatus.Up => NetworkState.Active,
                    System.Net.NetworkInformation.OperationalStatus.Down or System.Net.NetworkInformation.OperationalStatus.LowerLayerDown => NetworkState.Disabled,
                    System.Net.NetworkInformation.OperationalStatus.Dormant => NetworkState.Idle,
                    _ => null,
                };

                // Note that iface.NetworkInterfaceType is broken on Unix and cannot be used (.NET 5-6)
                if (iface.Name.StartsWith('w'))
                {
                    try
                    {
                        // Get WiFi signal
                        string wifiData = File.ReadAllText("/proc/net/wireless");
                        Regex signalRegex = new(iface.Name + @".*(-\d+)\.");
                        Match signalMatch = signalRegex.Match(wifiData);
                        if (signalMatch.Success)
                        {
                            networkInterface.RSSI = int.Parse(signalMatch.Groups[1].Value);
                        }

                        // Get WiFi SSID
                        if (File.Exists("/usr/sbin/iwgetid"))
                        {
                            ProcessStartInfo startInfo = new()
                            {
                                FileName = "/usr/sbin/iwgetid",
                                Arguments = $"{iface.Name} -r",
                                RedirectStandardOutput = true
                            };

                            using Process? process = Process.Start(startInfo);
                            if (process is not null)
                            {
                                string ssid = string.Empty;
                                process.OutputDataReceived += (sender, e) => ssid += e.Data;
                                process.BeginOutputReadLine();
                                await process.WaitForExitAsync(cancellationToken);
                                networkInterface.SSID = ssid;
                            }
                            else
                            {
                                networkInterface.SSID = string.Empty;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        networkInterface.RSSI = null;
                        networkInterface.SSID = string.Empty;
                        logger.LogDebug(e, "Failed to get WiFi data for interface {InterfaceName}", iface.Name);
                    }
                    networkInterface.Type = NetworkInterfaceType.WiFi;
                    networkInterface.WifiCountry = wifiCountry;
                }
                else
                {
                    networkInterface.RSSI = null;
                    networkInterface.SSID = null;
                    networkInterface.Type = NetworkInterfaceType.LAN;
                    networkInterface.WifiCountry = null;
                }
                networkInterface.PropertyChanged -= InterfaceUpdated;
            }
        }

        for (int i = model.Network.Interfaces.Count; i > index; i--)
        {
            model.Network.Interfaces.RemoveAt(i - 1);
            networkUpdated = true;
        }

        return networkUpdated;
    }

    [GeneratedRegex(@"^cpu\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)")]
    private static partial Regex _cpuRegex();

    [GeneratedRegex(@"^MemAvailable:\s*(\d+)( kB| KiB)", RegexOptions.IgnoreCase)]
    private static partial Regex _availableMemoryRegex();

    /// <summary>
    /// Update SBC data key
    /// </summary>
    public void UpdateSbc()
    {
        Regex cpuRegex = _cpuRegex();
        Regex availableMemoryRegex = _availableMemoryRegex();
        try
        {
            // Compute average CPU load
            double? avgLoad = null;
            IEnumerable<string> statsInfo = File.ReadLines("/proc/stat");
            foreach (string line in statsInfo)
            {
                Match match = cpuRegex.Match(line);
                if (match.Success)
                {
                    double total = 0;
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        total += double.Parse(match.Groups[i].Value);
                    }
                    avgLoad = Math.Round(100 - 100 * double.Parse(match.Groups[4].Value) / total, 2);
                    break;
                }
            }
            model.SBC!.CPU.AvgLoad = (float?)avgLoad;

            // Try to get the CPU temperature
            if (File.Exists(settings.Value.CpuTemperaturePath))
            {
                model.SBC!.CPU.Temperature = float.Parse(File.ReadAllText(settings.Value.CpuTemperaturePath)) / settings.Value.CpuTemperatureDivider;
            }

            // Try to update memory stats
            long? availableMemory = null;
            if (File.Exists("/proc/meminfo"))
            {
                IEnumerable<string> memoryInfo = File.ReadAllLines("/proc/meminfo");
                foreach (string line in memoryInfo)
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
            model.SBC.Memory.Available = availableMemory;

            // Update current SBC uptime
            model.SBC.Uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0]);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to update SBC stats");
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
    private bool UpdateVolumes(DriveInfo[] drives)
    {
        bool volumesUpdated = false;
        void VolumeUpdated(object? sender, PropertyChangedEventArgs e) => volumesUpdated = true;

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

        // Update volume info
        int index = 0;
        foreach (DriveInfo drive in drives)
        {
            long totalSize;
            try
            {
                // On some systems this query causes an IOException...
                totalSize = drive.TotalSize;
            }
            catch (IOException)
            {
                totalSize = 0;
            }

            if (drive.DriveType != DriveType.Ram && totalSize > 0)
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

                volume.PropertyChanged += VolumeUpdated;
                volume.Capacity = (drive.DriveType == DriveType.Network) ? null : totalSize;
                volume.FreeSpace = (drive.DriveType == DriveType.Network) ? null : drive.AvailableFreeSpace;
                volume.Mounted = drive.IsReady;
                // It's a shame DriveInfo does not provide a correct VolumeLabel property and no device node, so we need to *guess* it more or less
                volume.Name = labelSymlinks.TryGetValue(Path.GetFileName(drive.RootDirectory.FullName), out string? label) ? label : (drive.VolumeLabel == "/" ? null : drive.VolumeLabel);
                volume.PartitionSize = (drive.DriveType == DriveType.Network) ? null : totalSize;
                volume.Path = drive.RootDirectory.FullName;
                volume.PropertyChanged -= VolumeUpdated;
            }
        }

        for (int i = model.Volumes.Count; i > index; i--)
        {
            model.Volumes.RemoveAt(i - 1);
            volumesUpdated = true;
        }

        return volumesUpdated;
    }

    /// <summary>
    /// Clean expired messages
    /// </summary>
    private void CleanMessages()
    {
        for (int i = model.Messages.Count - 1; i >= 0; i--)
        {
            if (model.Messages[i].Time - DateTime.Now > TimeSpan.FromSeconds(settings.Value.MaxMessageAge))
            {
                model.Messages.RemoveAt(i);
            }
        }
    }
}
