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
            DateTime lastUpdateTime = DateTime.Now;
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
                    UpdateVolumes(drives);
                    CleanMessages();
                }

                // Check if the system time has to be updated
                if (DateTime.Now - lastUpdateTime > TimeSpan.FromMilliseconds(Settings.HostUpdateInterval + 5000) && !Debugger.IsAttached)
                {
                    _logger.Info("System time has been changed");
                    Code code = new()
                    {
                        InternallyProcessed = !Settings.NoSpi,
                        Flags = CodeFlags.Asynchronous,
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
                        InternallyProcessed = !Settings.NoSpi,
                        Flags = CodeFlags.Asynchronous,
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
                lastUpdateTime = DateTime.Now;
                await Task.Delay(Settings.HostUpdateInterval, Program.CancellationToken);
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Update network interfaces
        /// </summary>
        private static void UpdateNetwork(System.Net.NetworkInformation.NetworkInterface[] networkInterfaces)
        {
            int index = 0;
            foreach (System.Net.NetworkInformation.NetworkInterface iface in networkInterfaces)
            {
                if (iface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    DuetAPI.ObjectModel.NetworkInterface networkInterface;
                    if (index >= Provider.Get.Network.Interfaces.Count)
                    {
                        networkInterface = new DuetAPI.ObjectModel.NetworkInterface();
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
                    IPAddress ipAddress = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                           where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                           select unicastAddress.Address).FirstOrDefault();
                    IPAddress netMask = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                         where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                         select unicastAddress.IPv4Mask).FirstOrDefault();
                    IPAddress gateway = (from gatewayAddress in iface.GetIPProperties().GatewayAddresses
                                         where gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                         select gatewayAddress.Address).FirstOrDefault();
                    IPAddress dnsServer = (from item in iface.GetIPProperties().DnsAddresses
                                           where item.AddressFamily == AddressFamily.InterNetwork
                                           select item).FirstOrDefault();
                    // In theory we could use ipInfo.DhcpLeaseLifetime to check if DHCP is configured but it isn't supported on Unix (.NET 5)
                    networkInterface.ActualIP = networkInterface.ConfiguredIP = ipAddress?.ToString();
                    networkInterface.Subnet = netMask?.ToString();
                    networkInterface.Gateway = gateway?.ToString();
                    networkInterface.DnsServer = dnsServer?.ToString();
                    networkInterface.Mac = BitConverter.ToString(iface.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':');
                    networkInterface.Speed = (int?)(iface.Speed / 1000000);
                    networkInterface.Type = iface.Name.StartsWith("w") ? DuetAPI.ObjectModel.NetworkInterfaceType.WiFi : DuetAPI.ObjectModel.NetworkInterfaceType.LAN;

                    // Get WiFi-specific values.
                    // Note that iface.NetworkInterfaceType is broken on Unix and cannot be used (.NET 5)
                    if (iface.Name.StartsWith('w'))
                    {
                        try
                        {
                            string wifiData = File.ReadAllText("/proc/net/wireless");
                            Regex signalRegex = new(iface.Name + @".*(-\d+)\.");
                            Match signalMatch = signalRegex.Match(wifiData);
                            if (signalMatch.Success)
                            {
                                networkInterface.Signal = int.Parse(signalMatch.Groups[1].Value);
                            }
                        }
                        catch (Exception e)
                        {
                            networkInterface.Signal = null;
                            _logger.Debug(e);
                        }
                        networkInterface.Type = DuetAPI.ObjectModel.NetworkInterfaceType.WiFi;
                    }
                    else
                    {
                        networkInterface.Signal = null;
                        networkInterface.Type = DuetAPI.ObjectModel.NetworkInterfaceType.LAN;
                    }
                }
            }

            for (int i = Provider.Get.Network.Interfaces.Count; i > index; i--)
            {
                Provider.Get.Network.Interfaces.RemoveAt(i - 1);
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
