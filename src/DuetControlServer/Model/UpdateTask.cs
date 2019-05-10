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
                bool changedModel = false;
                using (await Provider.AccessReadWrite())
                {
                    MachineModel model = Provider.Get;
                    changedModel |= UpdateNetwork(ref model);
                    changedModel |= UpdateStorages(ref model);
                    changedModel |= ClearMessages(ref model);
                }

                // Notify the model subscribers
                if (changedModel)
                {
                    await IPC.Processors.Subscription.ModelUpdated();
                }

                // Wait for next update schedule
                await Task.Delay(Settings.HostUpdateInterval, Program.CancelSource.Token);
            } while (!Program.CancelSource.IsCancellationRequested);
        }

        private static bool UpdateNetwork(ref MachineModel model)
        {
            bool changed = false;

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

                    if (model.Network.Interfaces.Count <= index)
                    {
                        // Add new network interface
                        model.Network.Interfaces.Add(new DuetAPI.Machine.NetworkInterface
                        {
                            MacAddress = macAddress,
                            ActualIP = ipAddress,
                            ConfiguredIP = ipAddress,
                            Subnet = subnet,
                            Gateway = gateway,
                            Type = type
                        });
                        changed = true;
                    }
                    else
                    {
                        // Update existing entry
                        DuetAPI.Machine.NetworkInterface existing = model.Network.Interfaces[index];
                        if (existing.MacAddress != macAddress ||
                            existing.ActualIP != ipAddress ||
                            existing.ConfiguredIP != ipAddress ||
                            existing.Subnet != subnet ||
                            existing.Gateway != gateway ||
                            existing.Type != type)
                        {
                            existing.MacAddress = macAddress;
                            existing.ActualIP = ipAddress;
                            existing.ConfiguredIP = ipAddress;
                            existing.Subnet = subnet;
                            existing.Type = type;
                            changed = true;
                        }
                    }
                    index++;
                }
            }

            for (int i = model.Network.Interfaces.Count - 1; i > index; i--)
            {
                model.Network.Interfaces.RemoveAt(i);
                changed = true;
            }

            return changed;
        }

        // Note: Storage 0 always represents the root (/) on Linux. The following code achieves this but it
        // might need further adjustments to ensure this on every Linux distribution
        private static bool UpdateStorages(ref MachineModel model)
        {
            bool changed = false;

            int index = 0;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Ram && drive.TotalSize > 0)
                {
                    long? capacity = (drive.DriveType == DriveType.Network) ? null : (long?)drive.TotalSize;
                    long? free = (drive.DriveType == DriveType.Network) ? null : (long?)drive.AvailableFreeSpace;

                    if (model.Storages.Count <= index)
                    {
                        // Add new storage device
                        model.Storages.Add(new Storage
                        {
                            Capacity = capacity,
                            Free = free,
                            Mounted = drive.IsReady,
                            Path = drive.VolumeLabel

                        });
                        changed = true;
                    }
                    else
                    {
                        Storage existing = model.Storages[index];
                        if (existing.Capacity != capacity ||
                            existing.Free != free ||
                            existing.Mounted != drive.IsReady ||
                            existing.Path != drive.VolumeLabel)
                        {
                            existing.Capacity = capacity;
                            existing.Free = free;
                            existing.Mounted = drive.IsReady;
                            existing.Path = drive.VolumeLabel;
                            changed = true;
                        }
                    }
                    index++;
                }
            }

            for (int i = model.Network.Interfaces.Count - 1; i > index; i--)
            {
                model.Network.Interfaces.RemoveAt(i);
                changed = true;
            }

            return changed;
        }

        private static bool ClearMessages(ref MachineModel model)
        {
            bool changed = false;
            for (int i = model.Messages.Count - 1; i >= 0; i--)
            {
                if (model.Messages[i].Time - DateTime.Now > TimeSpan.FromSeconds(Settings.MaxMessageAge))
                {
                    model.Messages.RemoveAt(i);
                    changed = true;
                }
            }
            return changed;
        }
    }
}
