using DuetAPI.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NetworkInterface = System.Net.NetworkInformation.NetworkInterface;

namespace DuetPiManagementPlugin.Network
{
    /// <summary>
    /// Functions for WiFi network management via wpa_supplicant
    /// </summary>
    public static class WpaSupplicant
    {
        /// <summary>
        /// Start wpa_supplicant for station mode
        /// </summary>
        /// <returns>Start result</returns>
        public static async Task<string> Start()
        {
            StringBuilder builder = new();
            if (!await Command.ExecQuery("systemctl", "is-enabled -q wpa_supplicant.service"))
            {
                builder.AppendLine(await DHCP.SetIPAddress("wlan0", null, null, null, null));
                builder.AppendLine(await Command.Execute("systemctl", "start wpa_supplicant.service"));
                builder.AppendLine(await Command.Execute("systemctl", "enable -q wpa_supplicant.service"));
            }
            return builder.ToString().Trim();
        }

        /// <summary>
        /// Stop wpa_supplicant for station mode
        /// </summary>
        /// <returns>Stop result</returns>
        public static async Task<string> Stop()
        {
            StringBuilder builder = new();
            if (await Command.ExecQuery("systemctl", "is-enabled -q wpa_supplicant.service"))
            {
                builder.AppendLine(await Command.Execute("systemctl", "stop wpa_supplicant.service"));
                builder.AppendLine(await Command.Execute("systemctl", "disable -q wpa_supplicant.service"));
            }
            return builder.ToString().Trim();
        }

        /// <summary>
        /// Retrieve a list of configured SSIDs
        /// </summary>
        /// <returns>List of configured SSIDs</returns>
        public static async Task<List<string>> GetSSIDs()
        {
            List<string> ssids = [];
            if (File.Exists("/etc/wpa_supplicant/wpa_supplicant.conf"))
            {
                await using FileStream configStream = new("/etc/wpa_supplicant/wpa_supplicant.conf", FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(configStream);

                bool inNetworkSection = false;
                string? ssid = null;
                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    string trimmedLine = line.Trim();
                    if (inNetworkSection)
                    {
                        if (ssid is null)
                        {
                            if (trimmedLine.StartsWith("ssid="))
                            {
                                // Parse next SSID
                                ssid = trimmedLine["ssid=".Length..].Trim(' ', '\t', '"');
                            }
                        }
                        else if (trimmedLine.StartsWith('}'))
                        {
                            if (ssid is not null)
                            {
                                ssids.Add(ssid);
                                ssid = null;
                            }
                            inNetworkSection = false;
                        }
                    }
                    else if (trimmedLine.StartsWith("network={"))
                    {
                        inNetworkSection = true;
                    }
                }

                if (ssid is not null)
                {
                    ssids.Add(ssid);
                }
            }
            return ssids;
        }

        /// <summary>
        /// Report the current WiFi stations
        /// </summary>
        /// <returns></returns>
        public static async Task<Message> Report()
        {
            List<string> ssids = await GetSSIDs();
            if (ssids.Count > 0)
            {
                StringBuilder builder = new();

                builder.AppendLine("Remembered networks:");
                foreach (string ssid in ssids)
                {
                    builder.AppendLine(ssid);
                }

                // Current IP address configuration
                foreach (NetworkInterface iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus == OperationalStatus.Up && iface.Name.StartsWith('w'))
                    {
                        IPAddress ipAddress = (from item in iface.GetIPProperties().UnicastAddresses
                                               where item.Address.AddressFamily == AddressFamily.InterNetwork
                                               select item.Address).FirstOrDefault() ?? IPAddress.Any;
                        IPAddress netMask = (from item in iface.GetIPProperties().UnicastAddresses
                                             where item.Address.AddressFamily == AddressFamily.InterNetwork
                                             select item.IPv4Mask).FirstOrDefault() ?? IPAddress.Any;
                        IPAddress gateway = (from item in iface.GetIPProperties().GatewayAddresses
                                             where item.Address.AddressFamily == AddressFamily.InterNetwork
                                             select item.Address).FirstOrDefault() ?? IPAddress.Any;
                        IPAddress dnsServer = (from item in iface.GetIPProperties().DnsAddresses
                                               where item.AddressFamily == AddressFamily.InterNetwork
                                               select item).FirstOrDefault() ?? IPAddress.Any;
                        builder.AppendLine($"IP={ipAddress} GW={gateway} NM={netMask} DNS={dnsServer}");
                        break;
                    }
                }

                // Done
                return new Message(MessageType.Success, builder.ToString().Trim());
            }

            // No networks available
            return new Message(MessageType.Success, "No remembered networks");
        }

        /// <summary>
        /// Try to read the country code from the wpa_supplicant config file
        /// </summary>
        /// <returns>Country code or null if not found</returns>
        public static async Task<string?> GetCountryCode()
        {
            if (File.Exists("/etc/wpa_supplicant/wpa_supplicant.conf"))
            {
                await using FileStream configStream = new("/etc/wpa_supplicant/wpa_supplicant.conf", FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(configStream);

                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    string trimmedLine = line.Trim();
                    if (trimmedLine.StartsWith("country="))
                    {
                        // Country code found
                        return trimmedLine["country=".Length..].Trim(' ', '\t');
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Update a given SSID or add it to the configuration, or delete either a single or all saved SSIDs
        /// </summary>
        /// <param name="ssid">SSID to update or an asterisk with password set to null to delete all the profiles</param>
        /// <param name="psk">Password of the new network or null to delete it</param>
        /// <param name="countryCode">Optional country code, must be set if no country code is present yet</param>
        /// <returns>Update result</returns>
        public static async Task<Message> UpdateSSID(string? ssid, string? psk, string? countryCode = null)
        {
            // Create template if it doesn't already exist or if the 
            if (!File.Exists("/etc/wpa_supplicant/wpa_supplicant.conf"))
            {
                if (string.IsNullOrWhiteSpace(countryCode))
                {
                    return new Message(MessageType.Error, "WiFi country is unset. Please use M587 C to specify your country code (e.g. M587 C\"US\")");
                }

                await using FileStream configTemplateStream = new("/etc/wpa_supplicant/wpa_supplicant.conf", FileMode.Create, FileAccess.Write);
                await using StreamWriter writer = new(configTemplateStream);
                await writer.WriteLineAsync($"country={countryCode}");
                await writer.WriteLineAsync( "ctrl_interface=DIR=/var/run/wpa_supplicant GROUP=netdev");
                await writer.WriteLineAsync( "update_config=1");
            }

            // Rewrite wpa_supplicant.conf as requested
            bool countrySeen = false;
            await using (FileStream configStream = new("/etc/wpa_supplicant/wpa_supplicant.conf", FileMode.Open, FileAccess.ReadWrite))
            {
                // Parse the existing config file
                await using MemoryStream newConfigStream = new();
                {
                    using StreamReader reader = new(configStream, leaveOpen: true);
                    await using StreamWriter writer = new(newConfigStream, leaveOpen: true);

                    StringBuilder? networkSection = null;
                    string? parsedSsid = null;
                    bool networkUpdated = false;

                    while (!reader.EndOfStream)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null)
                        {
                            break;
                        }

                        string trimmedLine = line.TrimStart();
                        if (trimmedLine.StartsWith("country=") && !countrySeen)
                        {
                            // Read country code, replace it if requested
                            await writer.WriteLineAsync(string.IsNullOrWhiteSpace(countryCode) ? line : $"country={countryCode}");
                            countrySeen = true;
                        }
                        else if (networkSection is not null)
                        {
                            // Dealing with the content of a network section
                            if (trimmedLine.StartsWith("ssid="))
                            {
                                // Parse SSID
                                parsedSsid = trimmedLine["ssid=".Length..].Trim(' ', '\t', '"');
                                networkSection.AppendLine(line);
                            }
                            else if (parsedSsid == ssid && trimmedLine.StartsWith("psk=") && psk is not null)
                            {
                                // Replace PSK
                                networkSection.AppendLine($"psk=\"{psk}\"");
                                networkUpdated = true;
                            }
                            else if (trimmedLine.StartsWith('}'))
                            {
                                // End of network section
                                networkSection.AppendLine(line);
                                if ((ssid != "*" && ssid != parsedSsid) || psk is not null)
                                {
                                    await writer.WriteAsync(networkSection);
                                }
                                networkSection = null;
                                parsedSsid = null;
                            }
                            else
                            {
                                // Copy everything else
                                networkSection.AppendLine(line);
                            }
                        }
                        else if (trimmedLine.StartsWith("network={"))
                        {
                            // Entering a new network section
                            networkSection = new StringBuilder();
                            networkSection.AppendLine(line);
                        }
                        else
                        {
                            // Copy everything else
                            await writer.WriteLineAsync(line);
                        }
                    }

                    // Add missing network if required
                    if (!networkUpdated && ssid is not null && psk is not null)
                    {
                        await writer.WriteLineAsync("network={");
                        await writer.WriteLineAsync($"    ssid=\"{ssid}\"");
                        await writer.WriteLineAsync($"    psk=\"{psk}\"");
                        await writer.WriteLineAsync("}");
                    }
                }

                // Truncate the old config file
                configStream.Seek(0, SeekOrigin.Begin);
                configStream.SetLength(newConfigStream.Length);

                // Insert the country code at the start if it was missing before
                if (!countrySeen && !string.IsNullOrWhiteSpace(countryCode))
                {
                    await using StreamWriter countryCodeWriter = new(configStream, leaveOpen: true);
                    await countryCodeWriter.WriteLineAsync($"country={countryCode}");
                    countrySeen = true;
                }

                // Overwrite the rest of the previous config
                newConfigStream.Seek(0, SeekOrigin.Begin);
                await newConfigStream.CopyToAsync(configStream);
            }

            // Generate a result
            Message result = new();
            if (!countrySeen)
            {
                result.Type = MessageType.Warning;
                result.Content = "No country code found in wpa_supplicant.conf, WiFi may not work";
            }

            // Restart the service to apply the new configuration
            string restartResult = await Command.Execute("systemctl", "restart wpa_supplicant.service");
            if (!string.IsNullOrWhiteSpace(restartResult))
            {
                result.Content += '\n' + restartResult;
            }
            return result;
        }
    }
}
