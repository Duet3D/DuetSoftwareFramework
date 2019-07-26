using DuetAPI.Machine;
using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Static class that updates the machine model in certain intervals
    /// </summary>
    public static class UpdateTask
    {
        /// <summary>
        /// Run model updates in a certain interval.
        /// This function updates host properties like network interfaces and storage devices
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task UpdatePeriodically()
        {
            do
            {
                // Run another update cycle
                using (await Provider.AccessReadWriteAsync())
                {
                    UpdateNetwork();
                    UpdateStorages();
                    ClearMessages();
                }

                // Wait for next update schedule
                await Task.Delay(Settings.HostUpdateInterval, Program.CancelSource.Token);
            } while (!Program.CancelSource.IsCancellationRequested);
        }

        private static void UpdateNetwork()
        {
            int index = 0;
            System.Net.NetworkInformation.NetworkInterface[] interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                UnicastIPAddressInformation ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                      where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                      select unicastAddress).FirstOrDefault();

                if (ipInfo != null && ipInfo.Address.ToString() != "127.0.0.1")
                {
                    string macAddress = iface.GetPhysicalAddress().ToString();
                    string ipAddress = ipInfo.Address.ToString();
                    string subnet = ipInfo.IPv4Mask.ToString();
                    string gateway = (from gatewayAddress in iface.GetIPProperties().GatewayAddresses
                                      where gatewayAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                      select gatewayAddress.Address.ToString()).FirstOrDefault();
                    InterfaceType type = iface.Name.StartsWith("w") ? InterfaceType.WiFi : InterfaceType.LAN;
                    // uint speed = (uint)(iface.Speed / 1000000),                // Unsupported in .NET Core 2.2 on Linux

                    if (index >= Provider.Get.Network.Interfaces.Count)
                    {
                        // Add new network interface
                        Provider.Get.Network.Interfaces.Add(new DuetAPI.Machine.NetworkInterface
                        {
                            MacAddress = macAddress,
                            ActualIP = ipAddress,
                            ConfiguredIP = ipAddress,
                            Subnet = subnet,
                            Gateway = gateway,
                            Type = type
                        });
                    }
                    else
                    {
                        // Update existing entry
                        DuetAPI.Machine.NetworkInterface existing = Provider.Get.Network.Interfaces[index];
                        existing.MacAddress = macAddress;
                        existing.ActualIP = ipAddress;
                        existing.ConfiguredIP = ipAddress;
                        existing.Subnet = subnet;
                        existing.Type = type;
                    }
                    index++;
                }
            }

            for (int i = Provider.Get.Network.Interfaces.Count - 1; i > index; i--)
            {
                Provider.Get.Network.Interfaces.RemoveAt(i);
            }
        }

        // Note: Storage 0 always represents the root (/) on Linux. The following code achieves this but it
        // might need further adjustments to ensure this on every Linux distribution
        private static void UpdateStorages()
        {
            int index = 0;
            long totalSize;

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    // On some systems this query causes an IOException...
                    totalSize = drive.TotalSize;
                }
                catch
                {
                    totalSize = 0;
                }

                if (drive.DriveType != DriveType.Ram && totalSize > 0)
                {
                    long? capacity = (drive.DriveType == DriveType.Network) ? null : (long?)totalSize;
                    long? free = (drive.DriveType == DriveType.Network) ? null : (long?)drive.AvailableFreeSpace;

                    if (index >= Provider.Get.Storages.Count)
                    {
                        // Add new storage device
                        Provider.Get.Storages.Add(new Storage
                        {
                            Capacity = capacity,
                            Free = free,
                            Mounted = drive.IsReady,
                            Path = drive.VolumeLabel
                        });
                    }
                    else
                    {
                        Storage existing = Provider.Get.Storages[index];
                        existing.Capacity = capacity;
                        existing.Free = free;
                        existing.Mounted = drive.IsReady;
                        existing.Path = drive.VolumeLabel;
                    }
                    index++;
                }
            }

            for (int i = Provider.Get.Network.Interfaces.Count - 1; i > index; i--)
            {
                Provider.Get.Network.Interfaces.RemoveAt(i);
            }
        }

        private static void ClearMessages()
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
