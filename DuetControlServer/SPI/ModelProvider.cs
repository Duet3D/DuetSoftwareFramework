using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace DuetControlServer.SPI
{
    public static class ModelProvider
    {
        // TODO: Make this thread-safe. Possibly this can be only achieved by returning clones but perhaps there is still another solution...
        public static DuetAPI.Machine.Model Current { get; private set; }

        public static void Update()
        {
            // Set new machine model
            Current = new DuetAPI.Machine.Model
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
                                                      where unicastAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                      select unicastAddress).FirstOrDefault();

                if (ipInfo != null && ipInfo.Address.ToString() != "127.0.0.1")
                {
                    Current.Network.Interfaces.Add(new DuetAPI.Machine.Network.NetworkInterface
                    {
                        ActualIP = ipInfo.Address.ToString(),
                        ConfiguredIP = ipInfo.Address.ToString(),
                        Subnet = ipInfo.IPv4Mask.ToString(),
                        // Speed = (uint)(iface.Speed / 1000000),                // Unsupported in .NET Core 2.2 on Linux
                        Type = iface.Name.StartsWith("w") ? DuetAPI.Machine.Network.InterfaceType.WiFi : DuetAPI.Machine.Network.InterfaceType.LAN
                    });
                }
            }
        }
    }
}