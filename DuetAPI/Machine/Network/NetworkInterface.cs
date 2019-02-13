using System;

namespace DuetAPI.Machine.Network
{
    public static class Type
    {
        public const string WiFi = "wifi";
        public const string LAN = "lan";
    }

    public static class Protocol
    {
        public const string HTTP = "http";
        public const string FTP = "ftp";
        public const string Telnet = "telnet";
    }

    public class NetworkInterface : ICloneable
    {
        public string Type { get; set; } = Network.Type.WiFi;
        public string FirmwareVersion { get; set; }
        public uint Speed { get; set; }                                         // MBit (0 if no link)
        public int? Signal { get; set; }                                        // only WiFi (dBm)
        public string ConfiguredIP { get; set; }
        public string ActualIP { get; set; }
        public string Subnet { get; set; }
        public uint? NumReconnects { get; set; }
        public string[] ActiveProtocols { get; set; } = new string[0];          // may hold entries from Protocol

        public object Clone()
        {
            return new NetworkInterface
            {
                Type = (Type != null) ? string.Copy(Type) : null,
                FirmwareVersion = (FirmwareVersion != null) ? string.Copy(FirmwareVersion) : null,
                Speed = Speed,
                Signal = Signal,
                ConfiguredIP = (ConfiguredIP != null) ? string.Copy(ConfiguredIP) : null,
                ActualIP = (ActualIP != null) ? string.Copy(ActualIP) : null,
                Subnet = (Subnet != null) ? string.Copy(Subnet) : null,
                NumReconnects = NumReconnects,
                ActiveProtocols = (string[])ActiveProtocols.Clone()
            };
        }
    }
}
