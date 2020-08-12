using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
                // Run another update cycle
                using (await Provider.AccessReadWriteAsync())
                {
                    UpdateNetwork();
                    if (!Settings.NoSpi)
                    {
                        UpdateVolumes();
                    }
                    CleanMessages();
                }

                // Check if the system time has to be updated
                if (DateTime.Now - lastUpdateTime > TimeSpan.FromMilliseconds(Settings.HostUpdateInterval + 5000) &&
                    !System.Diagnostics.Debugger.IsAttached)
                {
                    _logger.Info("System time has been changed");
                    Code code = new Code
                    {
                        InternallyProcessed = !Settings.NoSpi,
                        Flags = CodeFlags.Asynchronous,
                        Channel = CodeChannel.Trigger,
                        Type = CodeType.MCode,
                        MajorNumber = 905
                    };
                    code.Parameters.Add(new CodeParameter('P', DateTime.Now.ToString("yyyy-MM-dd")));
                    code.Parameters.Add(new CodeParameter('S', DateTime.Now.ToString("HH:mm:ss")));
                    await code.Execute();
                }

                // Check if the hostname has to be updated
                if (lastHostname != Environment.MachineName)
                {
                    _logger.Info("Hostname has been changed");
                    lastHostname = Environment.MachineName;
                    Code code = new Code
                    {
                        InternallyProcessed = !Settings.NoSpi,
                        Flags = CodeFlags.Asynchronous,
                        Channel = CodeChannel.Trigger,
                        Type = CodeType.MCode,
                        MajorNumber = 550
                    };
                    code.Parameters.Add(new CodeParameter('P', lastHostname));
                    await code.Execute();
                }

                // Wait for next scheduled update check
                lastUpdateTime = DateTime.Now;
                await Task.Delay(Settings.HostUpdateInterval, Program.CancellationToken);
            }
            while (!Program.CancelSource.IsCancellationRequested);
        }

        /// <summary>
        /// Update network interfaces
        /// </summary>
        private static void UpdateNetwork()
        {
            int index = 0;
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                UnicastIPAddressInformation ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                      where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                      select unicastAddress).FirstOrDefault();
                if (ipInfo != null && !System.Net.IPAddress.IsLoopback(ipInfo.Address))
                {
                    DuetAPI.ObjectModel.NetworkInterface networkInterface;
                    if (index >= Provider.Get.Network.Interfaces.Count)
                    {
                        networkInterface = new DuetAPI.ObjectModel.NetworkInterface();
                        Provider.Get.Network.Interfaces.Add(networkInterface);
                    }
                    else
                    {
                        networkInterface = Provider.Get.Network.Interfaces[index];
                    }
                    index++;

                    networkInterface.Mac = BitConverter.ToString(iface.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':');
                    networkInterface.ActualIP = ipInfo.Address.ToString();
                    networkInterface.ConfiguredIP = ipInfo.Address.ToString();
                    networkInterface.Subnet = ipInfo.IPv4Mask.ToString();
                    networkInterface.Gateway = (from gatewayAddress in iface.GetIPProperties().GatewayAddresses
                                                where gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                select gatewayAddress.Address.ToString()).FirstOrDefault();
                    networkInterface.Type = iface.Name.StartsWith("w") ? InterfaceType.WiFi : InterfaceType.LAN;
                    // networkInterface.Speed = (uint)(iface.Speed / 1000000);                // Unsupported in .NET Core 2.2 on Linux
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
        /// Volume 0 always represents the virtual SD card on Linux. The following code achieves this but it
        /// might need further adjustments to ensure this on every Linux distribution
        /// </remarks>
        private static void UpdateVolumes()
        {
            int index = 0;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
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

                    volume.Capacity = (drive.DriveType == DriveType.Network) ? null : (long?)totalSize;
                    volume.FreeSpace = (drive.DriveType == DriveType.Network) ? null : (long?)drive.AvailableFreeSpace;
                    volume.Mounted = drive.IsReady;
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
