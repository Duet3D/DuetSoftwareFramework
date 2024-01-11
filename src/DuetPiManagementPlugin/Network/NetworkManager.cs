using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// Functions to manage networking using nmcli
    /// </summary>
    public static class NetworkManager
    {
        /// <summary>
        /// Check if NetworkManager is active
        /// </summary>
        public static Task<bool> IsActive() => Command.ExecQuery("systemctl", "-q is-active NetworkManager.service");

        /// <summary>
        /// Get the WiFi country code using raspi-config
        /// </summary>
        /// <returns>WiFi country code or null</returns>
        public static async Task<string?> GetWiFiCountry()
        {
            string output = await Command.Execute("raspi-config", "nonint get_wifi_country");
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }

        /// <summary>
        /// Get the currently active network profile UUID or null if not applicable
        /// </summary>
        /// <param name="iface">Ethernet interface name</param>
        /// <returns>Profile UUID or null if not active</returns>
        private static async Task<string?> GetActiveProfile(string iface)
        {
            string output = await Command.Execute("nmcli", "-c no -t connection show --active");
            using StringReader reader = new(output);
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                // Example: Wired connection 1:b1f2a2d4-0a20-30c7-b586-8ca0c5b9101a:802-3-ethernet:eth0
                string[] args = line.Split(':');
                if (args.Length > 2)
                {
                    if (args[^1].Equals(iface))
                    {
                        return args[1];
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Connect to the given SSID if applicable, else to the next available SSID
        /// </summary>
        /// <param name="iface"></param>
        /// <param name="ssid"></param>
        /// <returns></returns>
        public static async Task<string> Connect(string iface, string? ssid = null)
        {
            if (ssid is null)
            {
                return await Command.Execute("nmcli", $"-c no -t device connect {iface}");
            }
            return await Command.Execute("nmcli", $"-c no -t device wifi connect {ssid} iface {iface}");
        }

        /// <summary>
        /// Disable the given network interface
        /// </summary>
        /// <param name="iface">Interface name</param>
        /// <returns>Disable result</returns>
        public static async Task<string> Disconnect(string iface)
        {
            return await Command.Execute("nmcli", $"-c no -t device disconnect {iface}");
        }

        private static async Task<string?> GetProfileProperty(string iface, string property)
        {
            string? activeProfile = await GetActiveProfile(iface);
            if (activeProfile is not null)
            {
                string output = await Command.Execute("nmcli", $"-c no -t connection show {activeProfile}");
                using StringReader reader = new(output);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    string[] args = line.Split(':');
                    if (args.Length > 0)
                    {
                        if (args[0] == property)
                        {
                            return string.Join(':', args[1..]);
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get the configured IP address of the given network interface
        /// </summary>
        /// <param name="iface">Name of the network interface</param>
        /// <returns>Configured netmask or 0.0.0.0 if not configured</returns>
        public static async Task<IPAddress> GetConfiguredIPAddress(string iface)
        {
            string? ipAddress = await GetProfileProperty(iface, "ipv4.addresses");
            if (ipAddress is not null && ipAddress.Contains('/'))
            {
                return IPAddress.Parse(ipAddress.Split('/')[0]);
            }
            return IPAddress.Any;
        }

        /// <summary>
        /// Get the configured netmask of the given network interface
        /// </summary>
        /// <param name="iface">Name of the network interface</param>
        /// <returns>Configured netmask or 0.0.0.0 if not configured</returns>
        public static async Task<IPAddress> GetConfiguredNetmask(string iface)
        {
            string? ipAddress = await GetProfileProperty(iface, "ipv4.addresses");
            if (ipAddress is not null && ipAddress.Contains('/'))
            {
                int cidr = int.Parse(ipAddress.Split('/')[1]);
                uint mask = (cidr == 0) ? 0 : uint.MaxValue << (32 - cidr);
                return new IPAddress(BitConverter.GetBytes(mask).Reverse().ToArray());
            }
            return new IPAddress(new byte[] { 255, 255, 255, 0 });
        }

        /// <summary>
        /// Get the configured gateway of the given network interface
        /// </summary>
        /// <param name="iface">Name of the network interface</param>
        /// <returns>Configured gateway or 0.0.0.0 if not configured</returns>
        public static async Task<IPAddress> GetConfiguredGateway(string iface)
        {
            string? gateway = await GetProfileProperty(iface, "ipv4.addresses");
            return !string.IsNullOrEmpty(gateway) ? IPAddress.Parse(gateway) : IPAddress.Any;
        }

        /// <summary>
        /// Get the configured DNS server of the given network interface
        /// </summary>
        /// <param name="iface">Name of the network interface</param>
        /// <returns>Configured DNS server or 0.0.0.0 if not configured</returns>
        public static async Task<IPAddress> GetConfiguredDNSServer(string iface)
        {
            string? dnsServers = await GetProfileProperty(iface, "ipv4.addresses");
            return !string.IsNullOrEmpty(dnsServers) ? IPAddress.Parse(dnsServers.Split(' ')[0]) : IPAddress.Any;
        }

        /// <summary>
        /// Update the IP address of the given network interface
        /// </summary>
        /// <param name="iface">Name of the network interface</param>
        /// <param name="ip">IP address or null if unchanged</param>
        /// <param name="netmask">Subnet mask or null if unchanged</param>
        /// <param name="gateway">Gateway or null if unchanged</param>
        /// <param name="netmask">Subnet mask or null if unchanged</param>
        /// <param name="dnsServer">Set IP address for AP mode</param>
        /// <returns>Asynchronous task</returns>
        public static async Task<string> SetIPAddress(string iface, IPAddress? ip, IPAddress? netmask, IPAddress? gateway, IPAddress? dnsServer, bool? forAP = null)
        {
            string? activeProfile = await GetActiveProfile(iface);

            bool applyChanges = false;
            string nmcliArgs = string.IsNullOrEmpty(activeProfile) ? $"-c no -t connection add con-name {iface} ifname {iface} type ethernet" : $"-c no -t connection mod {activeProfile}";

            if (IPAddress.Any.Equals(ip))
            {
                // Apply DHCP
                nmcliArgs += " ipv4.method auto ipv4.addresses \"\" ipv4.gateway \"\" ipv4.dns \"\"";
                applyChanges = true;
            }
            else if (ip is not null || netmask is not null || gateway is not null || dnsServer is not null)
            {
                // Set static adress
                nmcliArgs += " ipv4.method manual";

                // Update IPv4 address and/or netmask
                if (ip is not null || netmask is not null)
                {
                    if (ip is null)
                    {
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni?.Name == iface)
                            {
                                ip = (from unicastAddress in ni.GetIPProperties().UnicastAddresses
                                      where unicastAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                      select unicastAddress.Address).FirstOrDefault();
                            }
                        }
                        if (ip is null)
                        {
                            throw new ArgumentException("Failed to determine IP address required to set netmask");
                        }
                    }
                    int cidr = (netmask is not null) ? netmask.GetAddressBytes().Sum(val => Convert.ToString(val, 2).Count(c => c == '1')) : 24;
                    nmcliArgs += $" ipv4.address {ip}/{cidr}";
                }

                // Update gateway
                if (gateway is not null)
                {
                    nmcliArgs += $" ipv4.gateway {gateway}";
                }

                // Update DNS server
                if (dnsServer is not null)
                {
                    nmcliArgs += $" ipv4.dns {dnsServer}";
                }

                applyChanges = true;
            }

            // Apply changes
            StringBuilder result = new();
            if (applyChanges)
            {
                result.AppendLine(await Command.Execute("nmcli", nmcliArgs));
                result.AppendLine(await Command.Execute("nmcli", $"-c no -t device reapply {iface}"));
            }
            return result.ToString().Trim();
        }

        /// <summary>
        /// Get a dictionary of profile name vs. SSID
        /// </summary>
        /// <returns>Dictionary of profile name vs. SSID</returns>
        public static async Task<Dictionary<string, string>>GetWirelessProfiles()
        {
            Dictionary<string, string> result = new();
            if (Directory.Exists("/etc/NetworkManager/system-connections"))
            {
                foreach (string file in Directory.EnumerateFiles("/etc/NetworkManager/system-connections"))
                {
                    using FileStream fs = new(file, FileMode.Open, FileAccess.Read);
                    using StreamReader reader = new(fs);
                    string? line, ssid = null;
                    bool inWiFiSection = false, isInfrastructure = false;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        string[] args = line.Split('=').Select(arg => arg.Trim()).ToArray();
                        if (args.Length == 2)
                        {
                            if (inWiFiSection)
                            {
                                if (args[0].Equals("mode"))
                                {
                                    isInfrastructure = args[1].Equals("infrastructure");
                                }
                                else if (line.Equals("ssid"))
                                {
                                    ssid = args[1];
                                }

                                if (isInfrastructure && ssid is not null)
                                {
                                    // Info complete
                                    break;
                                }
                            }
                        }
                        else if (args.Length == 1)
                        {
                            if (args[0].Equals("[wifi]"))
                            {
                                inWiFiSection = true;
                            }
                            else if (args[0].StartsWith('['))
                            {
                                inWiFiSection = false;
                            }
                        }
                    }

                    if (isInfrastructure && ssid is not null)
                    {
                        // Only list access point SSID profiles
                        result.Add(Path.GetFileName(file), ssid);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Retrieve a list of configured SSIDs
        /// </summary>
        /// <returns>List of configured SSIDs</returns>
        public static async Task<List<string>> GetSSIDs() => (await GetWirelessProfiles()).Select(item => item.Value).ToList();

        /// <summary>
        /// Update a given SSID or add it to the configuration, or delete either a single or all saved SSIDs
        /// </summary>
        /// <param name="ssid">SSID to update or an asterisk with password set to null to delete all the profiles</param>
        /// <param name="psk">Password of the new network or null to delete it</param>
        /// <param name="countryCode">Optional country code, must be set if no country code is present yet</param>
        /// <returns>Update result</returns>
        public static async Task<Message> UpdateSSID(string? ssid, string? psk, string? countryCode = null)
        {
            StringBuilder result = new();

            // Update WiFi country code frist
            if (countryCode is not null)
            {
                result.AppendLine(await Command.Execute("raspi-config", $"nonint do_wifi_country {countryCode}"));
            }

            // Turn off hotspot if required
            if (await IsHotspotEnabled())
            {
                result.AppendLine(await Disconnect("wlan0"));
            }

            // Update SSID/PSK
            Dictionary<string, string> profiles = await GetWirelessProfiles();
            if (ssid == "*")
            {
                // Delete all saved SSIDs
                foreach (string profile in profiles.Keys)
                {
                    result.Append(await Command.Execute("nmcli", $"-c no -t connection delete \"{profile}\""));
                }
            }
            else if (ssid is not null)
            {
                // Try to update existing profile
                bool profileUpdated = false;
                foreach (var kv in profiles)
                {
                    if (kv.Value.Equals(ssid))
                    {
                        if (psk is null)
                        {
                            // Delete WiFi profile
                            result.AppendLine(await Command.Execute("nmcli", $"-c no -t connection delete {kv.Key}"));
                        }
                        else
                        {
                            // Update PSK
                            result.AppendLine(await Command.Execute("nmcli", $"-c no -t connection modify {kv.Key} 802-11-wireless-security.psk \"{psk}\""));
                        }

                        profileUpdated = true;
                        break;
                    }
                }

                // Add new profile and set it up
                if (!profileUpdated && psk is not null)
                {
                    if (string.IsNullOrWhiteSpace(psk))
                    {
                        result.AppendLine(await Command.Execute("nmcli", $"-c no -t connection device wifi connect {ssid}"));
                    }
                    else
                    {
                        result.AppendLine(await Command.Execute("nmcli", $"-c no -t device wifi connect {ssid} password \"{psk}\""));
                    }
                }
            }
            
            // Done
            return new Message(MessageType.Success, result.ToString().Trim());
        }

        /// <summary>
        /// Check if a hotspot is enabled
        /// </summary>
        /// <returns>True if it is enabled</returns>
        public static async Task<bool> IsHotspotEnabled() => await GetProfileProperty("wlan0", "802-11-wireless.mode") == "ap";

        private static string? _ssid = null, _psk = null;
        private static IPAddress _ipAddress = IPAddress.Any;
        private static int? _channel = null;

        /// <summary>
        /// Start a WiFi hotspot
        /// </summary>
        /// <param name="ssid">SSID</param>
        /// <param name="psk">Password</param>
        /// <param name="ipAddress">IP address</param>
        /// <param name="channel">Channel</param>
        /// <returns>Enable result</returns>
        public static void ConfigureHotspot(string ssid, string psk, IPAddress ipAddress, int channel)
        {
            if (ssid == "*")
            {
                _ssid = _psk = null;
                _ipAddress = IPAddress.Any;
                _channel = null;
            }
            else
            {
                _ssid = ssid;
                _psk = psk;
                _ipAddress = ipAddress;
                _channel = channel;
            }
        }

        /// <summary>
        /// Start a WiFi hotspot
        /// </summary>
        /// <returns>Enable result</returns>
        public static async Task<string> StartHotspot()
        {
            StringBuilder result = new();

            // Start hotspot
            string nmcliArgs = "-c no -t device wifi hotspot con-name Hotspot";
            if (!string.IsNullOrWhiteSpace(_ssid))
            {
                nmcliArgs += $" ssid {_ssid}";
            }
            if (!string.IsNullOrWhiteSpace(_psk))
            {
                nmcliArgs += $" password \"{_psk}\"";
            }
            if (_channel is not null)
            {
                nmcliArgs += $" band bg channel {_channel}";
            }
            result.AppendLine(await Command.Execute("nmcli", nmcliArgs));

            // Change the IP address
            if (!IPAddress.Any.Equals(_ipAddress))
            {
                result.AppendLine(await SetIPAddress("wlan0", _ipAddress, null, null, null));
            }

            return result.ToString().Trim();
        }

        /// <summary>
        /// Stop a WiFi hotspot
        /// </summary>
        /// <returns>Disable result</returns>
        public static async Task<string> StopHotspot() => await Disconnect("wlan0");
    }
}