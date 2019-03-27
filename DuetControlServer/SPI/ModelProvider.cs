using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DuetAPI.Machine.Network;
using DuetAPI.Machine.Storages;
using Model = DuetAPI.Machine.Model;
using NetworkInterface = System.Net.NetworkInformation.NetworkInterface;

namespace DuetControlServer.SPI
{
    public static class ModelProvider
    {
        // TODO: Make this thread-safe. Possibly this can be only achieved by returning clones but perhaps there is still another solution...
        public static Model Current { get; private set; }

        public static void Update()
        {
            // Set new machine model
            Current = new Model
            {
                Electronics =
                {
                    Type = "duet3",
                    Name = "Duet 3",
                    Revision = "0.5"
                },
                Network =
                {
                    Name = Environment.MachineName
                }
            };

            // Retrieve network info
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface iface in interfaces)
            {
                UnicastIPAddressInformation ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                      where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                      select unicastAddress).FirstOrDefault();

                if (ipInfo != null && ipInfo.Address.ToString() != "127.0.0.1")
                {
                    Current.Network.Interfaces.Add(new DuetAPI.Machine.Network.NetworkInterface
                    {
                        ActualIP = ipInfo.Address.ToString(),
                        ConfiguredIP = ipInfo.Address.ToString(),
                        Subnet = ipInfo.IPv4Mask.ToString(),
                        // Speed = (uint)(iface.Speed / 1000000),                // Unsupported in .NET Core 2.2 on Linux
                        Type = iface.Name.StartsWith("w") ? InterfaceType.WiFi : InterfaceType.LAN
                    });
                }
            }
            
            // Retrieve storage devices
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Ram && drive.TotalSize > 0)
                {
                    Current.Storages.Add(new Storage
                    {
                        Capacity = (drive.DriveType == DriveType.Network) ? null : (ulong?)drive.TotalSize,
                        Free = (drive.DriveType == DriveType.Network) ? null : (ulong?)drive.AvailableFreeSpace,
                        Mounted = drive.IsReady,
                        Path = drive.VolumeLabel
                    });
                }
            }
        }
    }
}