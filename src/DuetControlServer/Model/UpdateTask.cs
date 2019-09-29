using DuetAPI.Commands;
using DuetAPI.Machine;
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
                    CleanMessages();
                }

                // Wait for next update schedule
                DateTime lastUpdateTime = DateTime.Now;
                await Task.Delay(Settings.HostUpdateInterval, Program.CancelSource.Token);
                if (DateTime.Now - lastUpdateTime > TimeSpan.FromMilliseconds(Settings.HostUpdateInterval + 1000) &&
                    !System.Diagnostics.Debugger.IsAttached)
                {
                    // System time has been changed - adjust date and time on the Duet
                    Console.WriteLine("[info] System time has been changed");
                    Code code = new Code
                    {
                        InternallyProcessed = true,
                        Channel = DuetAPI.CodeChannel.Daemon,
                        Type = CodeType.MCode,
                        MajorNumber = 905
                    };
                    code.Parameters.Add(new CodeParameter('P', DateTime.Now.ToString("yyyy-MM-dd")));
                    code.Parameters.Add(new CodeParameter('S', DateTime.Now.ToString("HH:mm:ss")));
                    await code.Execute();
                }
            } while (!Program.CancelSource.IsCancellationRequested);
        }

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
                    DuetAPI.Machine.NetworkInterface networkInterface;
                    if (index >= Provider.Get.Network.Interfaces.Count)
                    {
                        networkInterface = new DuetAPI.Machine.NetworkInterface();
                        Provider.Get.Network.Interfaces.Add(networkInterface);
                    }
                    else
                    {
                        networkInterface = Provider.Get.Network.Interfaces[index];
                    }
                    index++;

                    networkInterface.MacAddress = iface.GetPhysicalAddress().ToString();
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

        // Note: Storage 0 always represents the root (/) on Linux. The following code achieves this but it
        // might need further adjustments to ensure this on every Linux distribution
        private static void UpdateStorages()
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
                catch
                {
                    totalSize = 0;
                }

                if (drive.DriveType != DriveType.Ram && totalSize > 0)
                {
                    Storage storage;
                    if (index >= Provider.Get.Storages.Count)
                    {
                        storage = new Storage();
                        Provider.Get.Storages.Add(storage);
                    }
                    else
                    {
                        storage = Provider.Get.Storages[index];
                    }
                    index++;

                    storage.Capacity = (drive.DriveType == DriveType.Network) ? null : (long?)totalSize;
                    storage.Free = (drive.DriveType == DriveType.Network) ? null : (long?)drive.AvailableFreeSpace;
                    storage.Mounted = drive.IsReady;
                    storage.Path = drive.VolumeLabel;
                }
            }

            for (int i = Provider.Get.Network.Interfaces.Count; i > index; i--)
            {
                Provider.Get.Network.Interfaces.RemoveAt(i - 1);
            }
        }

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
