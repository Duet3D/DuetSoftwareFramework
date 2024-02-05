using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Static class that updates the machine model in certain intervals
    /// </summary>
    public static class PeriodicUpdater
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// List of enabled protocols
        /// </summary>
        private static readonly List<NetworkProtocol> _activeProtocols = new();

        /// <summary>
        /// Check if the given protocol is enabled
        /// </summary>
        /// <param name="protocol">Protocol to check</param>
        /// <returns>True if the protocol is enabled</returns>
        public static bool IsProtocolEnabled(NetworkProtocol protocol)
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
        public static void ProtocolEnabled(NetworkProtocol protocol)
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
        internal static void ProtocolDisabled(NetworkProtocol protocol)
        {
            lock (_activeProtocols)
            {
                _activeProtocols.Remove(protocol);
            }
        }

        /// <summary>
        /// Run model updates in a certain interval.
        /// This function updates host properties like network interfaces and storage devices
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            TimeSpan measuredDelay = TimeSpan.Zero;
            string lastHostname = Environment.MachineName;
            do
            {
                // Prefetch the network and volume devices because this can take quite a while (> 1.5s)
                System.Net.NetworkInformation.NetworkInterface[] networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                DriveInfo[] drives = DriveInfo.GetDrives();

                // Run another update cycle
                using (await Provider.AccessReadWriteAsync())
                {
                    UpdateNetwork(networkInterfaces);
                    UpdateSbc();
                    UpdateVolumes(drives);
                    CleanMessages();
                }

                // Check if the system time has to be updated
                if (measuredDelay > TimeSpan.FromMilliseconds(Settings.HostUpdateInterval + 2000) && !Debugger.IsAttached)
                {
                    _logger.Info("System time has been changed");
                    Code code = new()
                    {
                        Flags = (Settings.NoSpi ? CodeFlags.None : CodeFlags.IsInternallyProcessed) | CodeFlags.Asynchronous,
                        Channel = CodeChannel.Trigger,
                        Type = CodeType.MCode,
                        MajorNumber = 905,
                        Parameters = new()
                        {
                            new('P', DateTime.Now.ToString("yyyy-MM-dd")),
                            new('S', DateTime.Now.ToString("HH:mm:ss"))
                        }
                    };
                    await code.Execute();
                }

                // Check if the hostname has to be updated
                if (lastHostname != Environment.MachineName)
                {
                    _logger.Info("Hostname has been changed");
                    lastHostname = Environment.MachineName;
                    Code code = new()
                    {
                        Flags = (Settings.NoSpi ? CodeFlags.None : CodeFlags.IsInternallyProcessed) | CodeFlags.Asynchronous,
                        Channel = CodeChannel.Trigger,
                        Type = CodeType.MCode,
                        MajorNumber = 550,
                        Parameters = new()
                        {
                            new('P', lastHostname)
                        }
                    };
                    await code.Execute();
                }

                // Wait for next scheduled update check
                DateTime lastUpdateTime = DateTime.Now;
                await Task.Delay(Settings.HostUpdateInterval, Program.CancellationToken);
                measuredDelay = DateTime.Now - lastUpdateTime;
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Update network interfaces
        /// </summary>
        private static async void UpdateNetwork(System.Net.NetworkInformation.NetworkInterface[] networkInterfaces)
        {
            // DCS does not maintain the WiFi country code, so we need to cache it if was populated before
            string? wifiCountry = Provider.Get.Network.Interfaces.FirstOrDefault(iface => iface.WifiCountry != null)?.WifiCountry;

            int index = 0;
            foreach (System.Net.NetworkInformation.NetworkInterface iface in networkInterfaces)
            {
                if (iface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    NetworkInterface networkInterface;
                    if (index >= Provider.Get.Network.Interfaces.Count)
                    {
                        networkInterface = new NetworkInterface();
                        Provider.Get.Network.Interfaces.Add(networkInterface);

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
                        networkInterface = Provider.Get.Network.Interfaces[index];
                    }
                    index++;

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
                        _logger.Debug(e, "Failed to get IPv4 configuration data");
                    }

                    // .NET cannot determine if DHCP is used for a given adapter on Linux, so use "ip -4 addr" to get the IPv4 address lifetime (if any)
                    string? ipAddr = ipAddress?.ToString();
                    if (ipAddr != null)
                    {
                        if (ipAddr != networkInterface.ActualIP && File.Exists("/usr/sbin/ip"))
                        {
                            try
                            {
                                using Process? proc = Process.Start(new ProcessStartInfo("/usr/sbin/ip", $"-4 address show dev {iface.Name}") { RedirectStandardOutput = true });
                                if (proc != null)
                                {
                                    await proc.WaitForExitAsync();

                                    // Static IPv4 addresses do not have limited lifetimes
                                    string output = await proc.StandardOutput.ReadToEndAsync();
                                    networkInterface.ConfiguredIP = output.Contains("valid_lft forever") ? ipAddr : "0.0.0.0";
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to query DHCP info via ip utility");
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
                                networkInterface.Signal = int.Parse(signalMatch.Groups[1].Value);
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
                                    await process.WaitForExitAsync(Program.CancellationToken);
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
                            networkInterface.Signal = null;
                            networkInterface.SSID = string.Empty;
                            _logger.Debug(e);
                        }
                        networkInterface.Type = NetworkInterfaceType.WiFi;
                        networkInterface.WifiCountry = wifiCountry;
                    }
                    else
                    {
                        networkInterface.Signal = null;
                        networkInterface.SSID = null;
                        networkInterface.Type = NetworkInterfaceType.LAN;
                        networkInterface.WifiCountry = null;
                    }
                }
            }

            for (int i = Provider.Get.Network.Interfaces.Count; i > index; i--)
            {
                Provider.Get.Network.Interfaces.RemoveAt(i - 1);
            }
        }

        /// <summary>
        /// Update SBC data key
        /// </summary>
        public static void UpdateSbc()
        {
            Regex cpuRegex = new(@"^cpu\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)");
            Regex availableMemoryRegex = new(@"^MemAvailable:\s*(\d+)( kB| KiB)", RegexOptions.IgnoreCase);
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
                Provider.Get.SBC!.CPU.AvgLoad = (float?)avgLoad;

                // Try to get the CPU temperature
                if (File.Exists(Settings.CpuTemperaturePath))
                {
                    Provider.Get.SBC!.CPU.Temperature = float.Parse(File.ReadAllText(Settings.CpuTemperaturePath)) / Settings.CpuTemperatureDivider;
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
                Provider.Get.SBC.Memory.Available = availableMemory;

                // Update current SBC uptime
                Provider.Get.SBC.Uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0]);
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to update SBC stats");
            }
        }

        /// <summary>
        /// Update volume devices
        /// </summary>
        /// <remarks>
        /// Volume 0 always represents the virtual SD card on DuetPi. The following code achieves this but it
        /// might need further adjustments to ensure this on every Linux distribution
        /// </remarks>
        private static void UpdateVolumes(DriveInfo[] drives)
        {
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
                    if (index >= Provider.Get.Volumes.Count)
                    {
                        volume = new Volume();
                        Provider.Get.Volumes.Add(volume);
                    }
                    else
                    {
                        volume = Provider.Get.Volumes[index];
                    }
                    index++;

                    volume.Capacity = (drive.DriveType == DriveType.Network) ? null : totalSize;
                    volume.FreeSpace = (drive.DriveType == DriveType.Network) ? null : drive.AvailableFreeSpace;
                    volume.Mounted = drive.IsReady;
                    volume.PartitionSize = (drive.DriveType == DriveType.Network) ? null : totalSize;
                    volume.Path = drive.VolumeLabel;
                }
            }

            for (int i = Provider.Get.Volumes.Count; i > index; i--)
            {
                Provider.Get.Volumes.RemoveAt(i - 1);
            }
        }

        /// <summary>
        /// Clean expired messages
        /// </summary>
        private static void CleanMessages()
        {
            for (int i = Provider.Get.Messages.Count - 1; i >= 0; i--)
            {
                if (Provider.Get.Messages[i].Time - DateTime.Now > TimeSpan.FromSeconds(Settings.MaxMessageAge))
                {
                    Provider.Get.Messages.RemoveAt(i);
                }
            }
        }
    }
}
