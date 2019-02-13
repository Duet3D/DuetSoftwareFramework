using System.Linq;
using System.Net.NetworkInformation;

namespace DuetControlServer.RepRapFirmware
{
    public static class Model
    {
        public static DuetAPI.Machine.Model Current { get; private set; }

        static Model()
        {
            Current = new DuetAPI.Machine.Model();
            Current.Electronics.Name = "Duet 3";

            // Retrieve network info
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface iface in interfaces)
            {
                UnicastIPAddressInformation ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                      where unicastAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                      select unicastAddress).FirstOrDefault();

                Current.Network.Interfaces.Add(new DuetAPI.Machine.Network.NetworkInterface()
                {
                    ActualIP = (ipInfo == null) ? "0.0.0.0" : ipInfo.Address.ToString(),
                    ConfiguredIP = (ipInfo == null) ? "0.0.0.0" : ipInfo.Address.ToString(),
                    Subnet = (ipInfo == null) ? "0.0.0.0" : ipInfo.IPv4Mask.ToString(),
                    Speed = (uint)(iface.Speed / 1000000),
                    Type = iface.Name.StartsWith("w") ? DuetAPI.Machine.Network.Type.WiFi : DuetAPI.Machine.Network.Type.LAN
                });
            }
        }

        public static void Update()
        {
            // TODO update things here that are unlikly to change
        }
    }
}