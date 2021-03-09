using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
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
                if (iface.NetworkInterfaceType != NetworkInterfaceType.Loopback && i++ == index)
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
            if (!PhysicalAddress.TryParse(macAddress.Replace(':', '-'), out PhysicalAddress parsedAddress))
            {
                throw new ArgumentException("Invalid MAC address");
            }

            StringBuilder result = new();
            NetworkInterface iface = Get(index);
            bool isUp = iface.OperationalStatus == OperationalStatus.Up;

            // Set link down (if needed)
            if (isUp)
            {
                string setDownResult = await Command.Execute("/usr/sbin/ip", $"link set dev {iface.Name} down");
                result.AppendLine(setDownResult);
            }

            // Update MAC address
            string setResult = await Command.Execute("/usr/sbin/ip", $"link set dev {iface.Name} address {BitConverter.ToString(parsedAddress.GetAddressBytes()).Replace('-', ':')}");
            result.AppendLine(setResult);

            // Set link up again (if needed)
            if (isUp)
            {
                string setUpResult = await Command.Execute("/usr/sbin/ip", $"link set dev {iface.Name} up");
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
        public static async Task<Message> SetConfig(int index, CodeParameter pParam, CodeParameter sParam)
        {
            NetworkInterface iface = Get(index);
            StringBuilder result = new();
            if (iface.Name.StartsWith('w'))
            {
                // WiFi interface
                if (sParam == null)
                {
                    // Report the status only if no valid S parameter is given
                    await Report(result, iface, index);
                }
                else if (sParam <= 0 || sParam > 2)
                {
                    // Disable WiFi services
                    result.AppendLine(await AccessPoint.Stop());
                    result.AppendLine(await WPA.Stop());

                    // Disable WiFi adapter
                    string linkResult = await Command.Execute("/usr/sbin/ip", $"link set {iface.Name} down");
                    result.AppendLine(linkResult);
                }
                else if (sParam == 1)
                {
                    // Is there a wpa_supplicant.conf?
                    if (!File.Exists("/etc/wpa_supplicant/wpa_supplicant.conf"))
                    {
                        return new Message(MessageType.Error, "No WiFi configuration found, use M587 to configure at least one SSID");
                    }

                    // No longer in AP mode
                    result.AppendLine(await AccessPoint.Stop());

                    // Disable the adapter
                    string disableResult = await Command.Execute("/usr/sbin/ip", $"link set {iface.Name} down");
                    result.AppendLine(disableResult);

                    // Start station mode
                    result.AppendLine(await WPA.Start());

                    // Enable the adapter again
                    string enableResult = await Command.Execute("/usr/sbin/ip", $"link set {iface.Name} up");
                    result.AppendLine(enableResult);

                    // Connect to the given SSID (if applicable)
                    if (pParam != null)
                    {
                        // Find the network index
                        string networkList = await Command.Execute("/usr/sbin/wpa_cli", "list_networks");
                        Regex ssidRegex = new($"^(\\d+)\\s+{Regex.Escape(sParam)}\\W", RegexOptions.IgnoreCase);

                        int networkIndex = -1;
                        using (StringReader reader = new(networkList))
                        {
                            do
                            {
                                string line = await reader.ReadLineAsync();
                                if (line == null)
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
                        string selectResult = await Command.Execute("/usr/sbin/wpa_cli", $"-i {iface.Name} select_network {networkIndex}");
                        if (selectResult.Trim() != "OK")
                        {
                            result.AppendLine(selectResult);
                        }
                    }
                    // else wpa_supplicant will connect to the next available network
                }
                else if (sParam == 2)
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
                    result.AppendLine(await WPA.Stop());

                    // Disable the adapter
                    string disableResult = await Command.Execute("/usr/sbin/ip", $"link set {iface.Name} down");
                    result.AppendLine(disableResult);

                    // Start AP mode. This will enable the adapter too
                    result.AppendLine(await AccessPoint.Start());
                }
            }
            else
            {
                // Ethernet interface
                if (pParam != null)
                {
                    // Set IP address
                    IPAddress ip = IPAddress.Parse(pParam);
                    string setResult = await DHCP.SetIPAddress(iface.Name, ip, null, null, null);
                    result.AppendLine(setResult);
                }

                if (sParam != null && (iface.OperationalStatus != OperationalStatus.Up) != sParam)
                {
                    // Enable or disable the adapter if required
                    result.AppendLine(await Command.Execute("/usr/sbin/ip", $"link set {iface.Name} {(sParam ? "up" : "down")}"));
                }
            }
            return new Message(MessageType.Success, result.ToString().Trim());
        }

        /// <summary>
        /// Set and/or report the network mask via M553
        /// </summary>
        /// <param name="index">Index of the network interface</param>
        /// <param name="netmask">Subnet mask</param>
        /// <returns>Configuration result</returns>
        public static async Task<string> ManageNetmask(int index, IPAddress netmask)
        {
            NetworkInterface iface = Get(index);
            if (netmask != null)
            {
                return await DHCP.SetIPAddress(iface.Name, null, netmask, null, null);
            }

            if (iface.OperationalStatus == OperationalStatus.Up)
            {
                UnicastIPAddressInformation ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                      where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                      select unicastAddress).FirstOrDefault();
                netmask = (ipInfo != null) ? ipInfo.IPv4Mask : IPAddress.Any;
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
        public static async Task<string> ManageGateway(int index, IPAddress gateway, IPAddress dnsServer)
        {
            NetworkInterface iface = Get(index);
            if (gateway != null || dnsServer != null)
            {
                return await DHCP.SetIPAddress(iface.Name, null, null, gateway, dnsServer);
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
        public static async ValueTask Report(StringBuilder builder, NetworkInterface iface, int index)
        {
            if (iface == null)
            {
                int i = 0;
                foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (item.NetworkInterfaceType != NetworkInterfaceType.Loopback && (index < 0 || index == i))
                    {
                        await Report(builder, item, i++);
                    }
                }
            }
            else
            {
                if (NetworkInterface.GetAllNetworkInterfaces().Count(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback) > 1)
                {
                    // Add labels if there is more than one available network interface
                    builder.Append($"Interface {index}: ");
                }

                if (iface.Name.StartsWith('w'))
                {
                    // WiFi interface
                    if (iface.OperationalStatus != OperationalStatus.Down)
                    {
                        UnicastIPAddressInformation ipInfo = (from unicastAddress in iface.GetIPProperties().UnicastAddresses
                                                              where unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork
                                                              select unicastAddress).FirstOrDefault();
                        if (ipInfo != null)
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
                    IPAddress configuredIP = await DHCP.GetConfiguredIPAddress(iface.Name);
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
    }
}
