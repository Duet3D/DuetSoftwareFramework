using DuetAPI.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network
{
    /// <summary>
    /// Functions for access point mode
    /// </summary>
    public static class AccessPoint
    {
        /// <summary>
        /// Check if the given adapter is in access point mode
        /// </summary>
        /// <param name="iface">Name of the interface</param>
        /// <returns>True if the adapter is in AP mode</returns>
        public static async Task<bool> IsEnabled()
        {
            return File.Exists("/etc/hostapd/wlan0.conf") && (await Command.ExecQuery("systemctl", "is-active -q hostapd@wlan0.service"));
        }

        /// <summary>
        /// Start AP mode
        /// </summary>
        /// <returns>Start result</returns>
        public static async Task<string> Start()
        {
            StringBuilder builder = new();
            if (!await Command.ExecQuery("systemctl", "is-active -q hostapd@wlan0.service"))
            {
                builder.AppendLine(await Command.Execute("systemctl", "start hostapd@wlan0.service"));
                builder.AppendLine(await Command.Execute("systemctl", "enable -q hostapd@wlan0.service"));
            }
            if (!await Command.ExecQuery("systemctl", "is-active -q dnsmasq.service"))
            {
                builder.AppendLine(await Command.Execute("systemctl", "start dnsmasq.service"));
                builder.AppendLine(await Command.Execute("systemctl", "enable -q dnsmasq.service"));
            }
            return builder.ToString().Trim();
        }

        /// <summary>
        /// Stop AP mode
        /// </summary>
        /// <returns>Stop result</returns>
        public static async Task<string> Stop()
        {
            StringBuilder builder = new();
            if (await Command.ExecQuery("systemctl", "is-active -q hostapd@wlan0.service"))
            {
                builder.AppendLine(await Command.Execute("systemctl", "stop hostapd@wlan0.service"));
                builder.AppendLine(await Command.Execute("systemctl", "disable -q hostapd@wlan0.service"));
            }
            if (await Command.ExecQuery("systemctl", "is-active -q dnsmasq.service"))
            {
                builder.AppendLine(await Command.Execute("systemctl", "stop dnsmasq.service"));
                builder.AppendLine(await Command.Execute("systemctl", "disable -q dnsmasq.service"));
            }
            return builder.ToString().Trim();
        }

        /// <summary>
        /// Configure access point mode
        /// </summary>
        /// <param name="ssid">SSID to use</param>
        /// <param name="psk">Password to use</param>
        /// <param name="ipAddress">IP address</param>
        /// <param name="channel">Optional channel number</param>
        /// <returns></returns>
        public static async Task<Message> Configure(string ssid, string psk, IPAddress ipAddress, int channel)
        {
            string countryCode = await WPA.GetCountryCode();
            if (string.IsNullOrWhiteSpace(countryCode))
            {
                return new Message(MessageType.Error, "Cannot configure access point because no country code has been set. Use M587 C to set it first");
            }

            if (ssid == "*")
            {
                // Delete configuration files again
                bool fileDeleted = false;
                if (File.Exists("/etc/hostapd/wlan0.conf"))
                {
                    File.Delete("/etc/hostapd/wlan0.conf");
                    fileDeleted = true;
                }

                if (File.Exists("/etc/dnsmasq.conf"))
                {
                    File.Delete("/etc/dnsmasq.conf");
                    fileDeleted = true;
                }

                if (fileDeleted)
                {
                    // Reset IP address configuration to station mode
                    await WPA.SetIPAddress(IPAddress.Any, null, null, null);
                }
            }
            else
            {
                // Write hostapd config
                using (FileStream hostapdTemplateStream = new(Path.Combine(Directory.GetCurrentDirectory(), "hostapd.conf"), FileMode.Open, FileAccess.Read))
                {
                    using StreamReader reader = new(hostapdTemplateStream);
                    using FileStream hostapdConfigStream = new("/etc/hostapd/wlan0.conf", FileMode.Create, FileAccess.Write);
                    using StreamWriter writer = new(hostapdConfigStream);

                    while (!reader.EndOfStream)
                    {
                        string line = await reader.ReadLineAsync();
                        line = line.Replace("{ssid}", ssid);
                        line = line.Replace("{psk}", psk);
                        line = line.Replace("{channel}", channel.ToString());
                        line = line.Replace("{countryCode}", countryCode);
                        await writer.WriteLineAsync(line);
                    }
                }

                // Write dnsmasq config
                using (FileStream dnsmasqTemplateStream = new(Path.Combine(Directory.GetCurrentDirectory(), "dnsmasq.conf"), FileMode.Open, FileAccess.Read))
                {
                    using StreamReader reader = new(dnsmasqTemplateStream);
                    using FileStream dnsmasqConfigStream = new("/etc/dnsmasq.conf", FileMode.Create, FileAccess.Write);
                    using StreamWriter writer = new(dnsmasqConfigStream);

                    byte[] ip = ipAddress.GetAddressBytes();
                    string ipRangeStart = $"{ip[0]}.{ip[1]}.{ip[2]}.{((ip[3] < 100 || ip[3] > 150) ? 100 : 151)}";
                    string ipRangeEnd = $"{ip[0]}.{ip[1]}.{ip[2]}.{((ip[3] < 100 || ip[3] > 150) ? 150 : 200)}";

                    while (!reader.EndOfStream)
                    {
                        string line = await reader.ReadLineAsync();
                        line = line.Replace("{ipRangeStart}", ipRangeStart);
                        line = line.Replace("{ipRangeEnd}", ipRangeEnd);
                        line = line.Replace("{ipAddress}", ipAddress.ToString());
                        await writer.WriteLineAsync(line);
                    }
                }

                // Set IP address configuration for AP mode
                await WPA.SetIPAddress(ipAddress, null, null, null, true);
            }

            // Done
            return new Message();
        }
    }
}
