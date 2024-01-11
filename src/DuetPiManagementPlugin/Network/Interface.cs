using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NetworkInterface = System.Net.NetworkInformation.NetworkInterface;

namespace DuetPiManagementPlugin.Network
{
    /// <summary>
    /// Functions to manage network interfaces
    /// </summary>
    public static class Interface
    {
        /// <summary>
        /// Get the network interface by index
        /// </summary>
        /// <param name="index">Index of the network interface</param>
        /// <returns>Network interface</returns>
        public static NetworkInterface Get(int index)
        {
            int i = 0;
            foreach (NetworkInterface iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback && i++ == index)
                {
                    return iface;
                }
            }
            throw new ArgumentOutOfRangeException(nameof(index), "Invalid network interface");
        }

        /// <summary>
        /// Assign a custom MAC address to a network adapter
        /// </summary>
        /// <param name="index">Index of the network interface</param>
        /// <param name="macAddress">MAC address to set</param>
        /// <returns>Command result</returns>
        public static async Task<Message> SetMACAddress(int index, string macAddress)
        {
            if (!PhysicalAddress.TryParse(macAddress.Replace(':', '-'), out PhysicalAddress? parsedAddress))
            {
                throw new ArgumentException("Invalid MAC address");
            }

            StringBuilder result = new();
            NetworkInterface iface = Get(index);
            bool isUp = iface.OperationalStatus == OperationalStatus.Up;

            // Set link down (if needed)
            if (isUp)
            {
                string setDownResult = await Command.Execute("ip", $"link set dev {iface.Name} down");
                result.AppendLine(setDownResult);
            }

            // Update MAC address
            string setResult = await Command.Execute("ip", $"link set dev {iface.Name} address {BitConverter.ToString(parsedAddress.GetAddressBytes()).Replace('-', ':')}");
            result.AppendLine(setResult);

            // Set link up again (if needed)
            if (isUp)
            {
                string setUpResult = await Command.Execute("ip", $"link set dev {iface.Name} up");
                result.AppendLine(setUpResult);
            }

            return new Message(MessageType.Success, result.ToString().Trim()); 
        }

        /// <summary>
        /// Manage the given network interface via M552
        /// </summary>
        /// <param name="index">Index of the network interface</param>
        /// <param name="pParam">P parameter</param>
        /// <param name="sParam">S parameter</param>
        /// <returns>Configuration result</returns>
        public static async Task<Message> SetConfig(int index, string? pParam, int? sParam)
        {
            NetworkInterface iface = Get(index);
            StringBuilder result = new();
            if (iface.Name.StartsWith('w'))
            {
                // WiFi interface
                if (sParam is null)
                {
                    // Report the status only if no valid S parameter is given
                    await Report(result, iface, index);
                }
                else if (sParam <= 0 || sParam > 2)
                {
                    // Disable AP mode
                    result.AppendLine(await AccessPoint.Stop());

                    // Disable current WiFi connection
                    if (await NetworkManager.IsActive())
                    {
                        result.AppendLine(await NetworkManager.Disconnect(iface.Name));
                    }
                    else
                    {
                        result.AppendLine(await WpaSupplicant.Stop());
                        result.AppendLine(await Command.Execute("ip", $"link set {iface.Name} down"));
                    }
                }
                else if (sParam == 1)
                {
                    // No longer in AP mode
                    result.AppendLine(await AccessPoint.Stop());

                    // Turn on the adapter
                    if (await NetworkManager.IsActive())
                    {
                        // Enable WiFi radio
                        string radioResult = await Command.Execute("nmcli", "radio wifi on");
                        result.AppendLine(radioResult);

                        // Connect (to the given AP name)
                        string connectResult = await NetworkManager.Connect(iface.Name, pParam);
                        result.AppendLine(connectResult);
                    }
                    else
                    {
                        // Is there a wpa_supplicant.conf?
                        if (!File.Exists("/etc/wpa_supplicant/wpa_supplicant.conf"))
                        {
                            return new Message(MessageType.Error, "No WiFi configuration found, use M587 to configure at least one SSID");
                        }

                        // Disable the adapter
                        string disableResult = await Command.Execute("ip", $"link set {iface.Name} down");
                        result.AppendLine(disableResult);

                        // Unblock WiFi
                        string unblockResult = await Command.Execute("rfkill", "unblock wifi");
                        result.AppendLine(unblockResult);

                        // Start station mode
                        string wpaResult = await WpaSupplicant.Start();
                        result.AppendLine(wpaResult);

                        // Enable the adapter again
                        string enableResult = await Command.Execute("ip", $"link set {iface.Name} up");
                        result.AppendLine(enableResult);

                        // Connect to the given SSID (if applicable)
                        if (pParam is not null)
                        {
                            // Find the network index
                            string networkList = await Command.Execute("wpa_cli", "list_networks");
                            Regex ssidRegex = new($"^(\\d+)\\s+{Regex.Escape(pParam)}\\W", RegexOptions.IgnoreCase);

                            int networkIndex = -1;
                            using (StringReader reader = new(networkList))
                            {
                                do
                                {
                                    string? line = await reader.ReadLineAsync();
                                    if (line is null)
                                    {
                                        break;
                                    }

                                    Match match = ssidRegex.Match(line);
                                    if (match.Success)
                                    {
                                        networkIndex = int.Parse(match.Groups[1].Value);
                                        break;
                                    }
                                }
                                while (!Program.CancellationToken.IsCancellationRequested);
                            }
                            if (networkIndex == -1)
                            {
                                return new Message(MessageType.Error, "SSID could not be found, use M587 to configure it first");
                            }

                            // Select it
                            string selectResult = await Command.Execute("wpa_cli", $"-i {iface.Name} select_network {networkIndex}");
                            if (selectResult.Trim() != "OK")
                            {
                                result.AppendLine(selectResult);
                            }
                        }
                        // else wpa_supplicant will connect to the next available network
                    }
                }
                else if (sParam == 2)
                {
                    if (!await NetworkManager.IsActive())
                    {
                        // Are the required config files present?
                        if (!File.Exists("/etc/hostapd/wlan0.conf"))
                        {
                            return new Message(MessageType.Error, "No hostapd configuration found, use M589 to configure the access point first");
                        }
                        if (!File.Exists("/etc/dnsmasq.conf"))
                        {
                            return new Message(MessageType.Error, "No dnsmasq configuration found, use M589 to configure the access point first");
                        }

                        // Is there at least one DHCP profile for AP mode?
                        if (!await DHCP.IsAPConfigured())
                        {
                            return new Message(MessageType.Error, "No access point configuration found. Use M587 to configure it first");
                        }

                        // No longer in station mode
                        result.AppendLine(await WpaSupplicant.Stop());

                        // Disable the adapter
                        string disableResult = await Command.Execute("ip", $"link set {iface.Name} down");
                        result.AppendLine(disableResult);
                    }

                    // Start AP mode. This will enable the adapter too
                    result.AppendLine(await AccessPoint.Start());
                }
            }
            else
            {
                // Ethernet interface
                if (pParam is not null)
                {
                    // Set IP address
                    IPAddress ip = IPAddress.Parse(pParam);
                    string setResult = (await NetworkManager.IsActive()) ? await NetworkManager.SetIPAddress(iface.Name, ip, null, null, null) : await DHCP.SetIPAddress(iface.Name, ip, null, null, null);
                    result.AppendLine(setResult);
                }

                if (sParam is not null && (iface.OperationalStatus == OperationalStatus.Up) != (sParam > 0))
                {
                    if (await NetworkManager.IsActive())
                    {
                        // Enable or disable the adapter via nmcli
                        result.AppendLine((sParam > 0) ? await NetworkManager.Connect(iface.Name) : await NetworkManager.Disconnect(iface.Name));
                    }
                    else
                    {
                        // Enable or disable the adapter if required
                        result.AppendLine(await Command.Execute("ip", $"link set {iface.Name} {(sParam > 0 ? "up" : "down")}"));
                    }
                }

                // We never get here if neither P nor S is present
            }
            return new Message(MessageType.Success, result.ToString().Trim());
        }

        /// <summary>
        /// Update the IP address of the WiFi network interface
        /// </summary>
        /// <param name="iface">Interface name</param>
        /// <param name="ip">IP address or null if unchanged</param>
        /// <param name="netmask">Subnet mask or null if unchanged</param>
        /// <param name="gateway">Gateway or null if unchanged</param>
        /// <param name="netmask">Subnet mask or null if unchanged</param>
        /// <param name="dnsServer">Set IP address for AP mode</param>
        /// <returns>Asynchronous task</returns>
        public static async Task<string> SetIPAddress(string iface, IPAddress? ip, IPAddress? netmask, IPAddress? gateway, IPAddress? dnsServer, bool forAP = false)
        {
            if (await NetworkManager.IsActive())
            {
                return await NetworkManager.SetIPAddress(iface, ip, netmask, gateway, dnsServer, forAP);
            }
            return await DHCP.SetIPAddress(iface, ip, netmask, gateway, dnsServer, forAP);
        }

        /// <summary>
        /// Set and/or report the network mask via M553
        /// </summary>
        /// <param name="index">Index of the network interface</param>
        /// <param name="netmask">Subnet mask</param>
        /// <returns>Configuration result</returns>
        public static async Task<string> ManageNetmask(int index, IPAddress? netmask)
        {
            NetworkInterface iface = Get(index);
            if (netmask is not null)
            {
                return await SetIPAddress(iface.Name, null, netmask, null, null);
            }

            if (iface.OperationalStatus == OperationalStatus.Up)
            {
                UnicastIPAddressInformation? ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                       where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                       select unicastAddress).FirstOrDefault();
                netmask = (ipInfo is not null) ? ipInfo.IPv4Mask : IPAddress.Any;
            }
            else if (await NetworkManager.IsActive())
            {
                netmask = await NetworkManager.GetConfiguredNetmask(iface.Name);
            }
            else
            {
                netmask = await DHCP.GetConfiguredNetmask(iface.Name);
            }
            return $"Net mask: {netmask}";
        }

        /// <summary>
        /// Set the network mask via M553
        /// </summary>
        /// <param name="index">Index of the network interface</param>
        /// <param name="netmask">Subnet mask</param>
        /// <returns>Configuration result</returns>
        public static async Task<string> ManageGateway(int index, IPAddress? gateway, IPAddress? dnsServer)
        {
            NetworkInterface iface = Get(index);
            if (gateway is not null || dnsServer is not null)
            {
                return await SetIPAddress(iface.Name, null, null, gateway, dnsServer);
            }

            if (iface.OperationalStatus == OperationalStatus.Up)
            {
                gateway = (from item in iface.GetIPProperties().GatewayAddresses
                           where item.Address.AddressFamily == AddressFamily.InterNetwork
                           select item.Address).FirstOrDefault() ?? IPAddress.Any;
                dnsServer = (from item in iface.GetIPProperties().DnsAddresses
                             where item.AddressFamily == AddressFamily.InterNetwork
                             select item).FirstOrDefault() ?? IPAddress.Any;
            }
            else if (await NetworkManager.IsActive())
            {
                gateway = await NetworkManager.GetConfiguredGateway(iface.Name);
                dnsServer = await NetworkManager.GetConfiguredDNSServer(iface.Name);
            }
            else
            {
                gateway = await DHCP.GetConfiguredGateway(iface.Name);
                dnsServer = await DHCP.GetConfiguredDNSServer(iface.Name);
            }

            StringBuilder builder = new();
            builder.AppendLine($"Gateway: {gateway}");
            builder.Append($"DNS server: {dnsServer}");
            return builder.ToString();
        }

        /// <summary>
        /// Report the IP address of the network interface(s)
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        /// <param name="iface">Optional network interface</param>
        /// <param name="index">Index of the network interface</param>
        public static async ValueTask Report(StringBuilder builder, NetworkInterface? iface, int index)
        {
            if (iface is null)
            {
                int i = 0;
                foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (item.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        if (index < 0 || index == i)
                        {
                            await Report(builder, item, i);
                        }
                        i++;
                    }
                }
            }
            else
            {
                if (NetworkInterface.GetAllNetworkInterfaces().Count(item => item.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback) > 1)
                {
                    // Add labels if there is more than one available network interface
                    builder.Append($"Interface {index}: ");
                }

                if (iface.Name.StartsWith('w'))
                {
                    // WiFi interface
                    if (iface.OperationalStatus != OperationalStatus.Down)
                    {
                        UnicastIPAddressInformation? ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                               where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                               select unicastAddress).FirstOrDefault();
                        if (ipInfo is not null)
                        {
                            bool isAccessPoint = await AccessPoint.IsEnabled();
                            builder.AppendLine($"WiFi module is {(isAccessPoint ? "providing access point" : "connected to access point")}, IP address {ipInfo.Address}");
                        }
                        else
                        {
                            builder.AppendLine("WiFi module is idle");
                        }
                    }
                    else
                    {
                        builder.AppendLine("WiFi module is disabled");
                    }
                }
                else
                {
                    // Ethernet interface
                    IPAddress configuredIP = (await NetworkManager.IsActive()) ? await NetworkManager.GetConfiguredIPAddress(iface.Name) : await DHCP.GetConfiguredIPAddress(iface.Name);
                    if (iface.OperationalStatus == OperationalStatus.Up)
                    {
                        IPAddress actualIP = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                              where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                              select unicastAddress.Address).FirstOrDefault() ?? IPAddress.Any;
                        builder.AppendLine($"Ethernet is enabled, configured IP address: {configuredIP}, actual IP address: {actualIP}");
                    }
                    else
                    {
                        builder.AppendLine($"Ethernet is disabled, configured IP address: {configuredIP}, actual IP address: 0.0.0.0");
                    }
                }
            }
        }

        /// <summary>
        /// Report the current WiFi stations
        /// </summary>
        /// <returns></returns>
        public static async Task<Message> ReportSSIDs()
        {
            List<string> ssids = (await NetworkManager.IsActive()) ? await NetworkManager.GetSSIDs() : await WpaSupplicant.GetSSIDs();
            if (ssids.Count > 0)
            {
                // List SSIDs
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
        /// Update a given SSID or add it to the configuration, or delete either a single or all saved SSIDs
        /// </summary>
        /// <param name="ssid">SSID to update or an asterisk with password set to null to delete all the profiles</param>
        /// <param name="psk">Password of the new network or null to delete it</param>
        /// <param name="countryCode">Optional country code, must be set if no country code is present yet</param>
        /// <returns>Update result</returns>
        public static async Task<Message> UpdateSSID(string? ssid, string? psk, string? countryCode = null)
        {
            if (await NetworkManager.IsActive())
            {
                return await NetworkManager.UpdateSSID(ssid, psk, countryCode);
            }
            return await WpaSupplicant.UpdateSSID(ssid, psk, countryCode);
        }
    }
}
