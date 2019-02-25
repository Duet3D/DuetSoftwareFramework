using System;

namespace DuetAPI.Machine.Network
{
    /// <summary>
    /// Supported types of network interfaces
    /// </summary>
    public static class Type
    {
        public const string WiFi = "wifi";
        public const string LAN = "lan";
    }

    /// <summary>
    /// Supported network protocols
    /// </summary>
    public static class Protocol
    {
        public const string HTTP = "http";
        public const string FTP = "ftp";
        public const string Telnet = "telnet";
    }

    /// <summary>
    /// Information about a network interface
    /// </summary>
    public class NetworkInterface : ICloneable
    {
        /// <summary>
        /// Type of this network interface
        /// </summary>
        /// <seealso cref="Type"/>
        public string Type { get; set; } = Network.Type.WiFi;
        
        /// <summary>
        /// Version of the network interface or null if unknown.
        /// This is primarily intended for the ESP8266-based network interfaces as used on the Duet WiFi
        /// </summary>
        public string FirmwareVersion { get; set; }
        
        /// <summary>
        /// Speed of the network interface (in MBit, null if unknown, 0 if not connected)
        /// </summary>
        public uint? Speed { get; set; }
        
        /// <summary>
        /// Signal of the WiFi adapter (only WiFi, in dBm)
        /// </summary>
        public int? Signal { get; set; }
        
        /// <summary>
        /// Configured IPv4 address of the network adapter
        /// </summary>
        public string ConfiguredIP { get; set; }
        
        /// <summary>
        /// Actual IPv4 address of the network adapter
        /// </summary>
        public string ActualIP { get; set; }
        
        /// <summary>
        /// Subnet of the network adapter
        /// </summary>
        public string Subnet { get; set; }
        
        /// <summary>
        /// Number of reconnect attempts or null if unknown
        /// </summary>
        public uint? NumReconnects { get; set; }
        
        /// <summary>
        /// List of active protocols
        /// </summary>
        /// <seealso cref="Protocol"/>
        public string[] ActiveProtocols { get; set; } = new string[0];

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
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
