using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network
{
    public static class WifiScan
    {
        private class WifiNetwork
        {
            public string Name { get; set; } = "n/a";
            public int Channel { get; set; } = -1;
            public int Rssi { get; set; } = 0;
            public string PhyMode { get; set; } = "n/a";
            public string Auth { get; set; } = "Open";
            public string MacAddress { get; set; } = "n/a";
        }

        private static bool _scanning;

        private static bool _scanFailed;

        private static List<WifiNetwork>? _networks;

        /// <summary>
        /// Start scanning for WiFi networks
        /// </summary>
        /// <returns>Message</returns>
        public static Message Start()
        {
            if (_scanning)
            {
                return new Message(MessageType.Error, "scan still in progress");
            }

            _scanning = true;
            _scanFailed = false;
            _ = Task.Run(ScanAsync);
            return new Message();
        }

        private static readonly Regex BssRegex = new(@"^BSS ([\d\w][\d\w]:[\d\w][\d\w]:[\d\w][\d\w]:[\d\w][\d\w]:[\d\w][\d\w]:[\d\w][\d\w])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SupportedRatesRegex = new(@"^(?:extended )?supported rates: (.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SsidRegex = new(@"^SSID: (.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ChannelRegex = new(@"(?:^DS Parameter set: channel|\* primary channel:) (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SignalRegex = new(@"^signal: (-\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WepRegex = new(@"capability:.*0x9[1234]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Perform WiFi scan. Instead of iw dev scan we could also consider using wpa_cli scan/scan_results,
        /// but that does not output anything about the encryption/phy types
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task ScanAsync()
        {
            try
            {
                // Unblock WiFi and enable the adapter
                if (!await Command.ExecQuery("rfkill", "unblock wifi") || !await Command.ExecQuery("ip", "link set wlan0 up"))
                {
                    _scanning = false;
                    _scanFailed = true;
                    return;
                }

                // Capture output from scan command
                string scanOutput;
                ProcessStartInfo startInfo = new("iw", "dev wlan0 scan") { RedirectStandardOutput = true };
                using (Process? process = Process.Start(startInfo))
                {
                    if (process is null)
                    {
                        _scanning = false;
                        _scanFailed = true;
                        return;
                    }
                    await process.WaitForExitAsync(Program.CancellationToken);

                    if (process.ExitCode != 0)
                    {
                        _scanning = false;
                        _scanFailed = true;
                        return;
                    }
                    scanOutput = await process.StandardOutput.ReadToEndAsync();
                }

                // Parser helpers
                _networks = [];
                WifiNetwork? network = null;

                int highestSupportedRate = 0;
                bool vht = false, ht = false, wpa = false, wpa2 = false, wpa3 = false;
                void AddNetwork()
                {
                    if (network is not null)
                    {
                        // Determine the phy mode from the supported rate, channel, and VHT/HT capabilities
                        if (highestSupportedRate > 11)
                        {
                            if (vht)
                            {
                                network.PhyMode = (network.Channel <= 14) ? "ax" : "ac";
                            }
                            else if (ht)
                            {
                                network.PhyMode = "n";
                            }
                            else
                            {
                                network.PhyMode = (network.Channel <= 14) ? "g" : "a";
                            }
                        }
                        else
                        {
                            network.PhyMode = "b";
                        }

                        // Determine WPA protection type if applicable
                        List<string> wpaProtection = [];
                        if (wpa)
                        {
                            wpaProtection.Add("WPA");
                        }
                        if (wpa2)
                        {
                            wpaProtection.Add("WPA2");
                        }
                        if (wpa3)
                        {
                            wpaProtection.Add("WPA3");
                        }
                        if (wpaProtection.Count > 0)
                        {
                            network.Auth = string.Join('/', wpaProtection);
                        }

                        // Add network
                        _networks!.Add(network);

                        // Reset collected data
                        highestSupportedRate = 0;
                        vht = ht = wpa = wpa2 = false;
                    }
                }

                // Parse output
                using StringReader reader = new(scanOutput);
                string? line;
                Match match;
                while ((line = reader.ReadLine()?.Trim()) != null)
                {
                    match = BssRegex.Match(line);
                    if (match.Success)
                    {
                        // BSS
                        AddNetwork();
                        network = new()
                        {
                            MacAddress = match.Groups[1].Value
                        };
                    }
                    else if (network is not null)
                    {
                        // (Extended) Supported Rates
                        match = SupportedRatesRegex.Match(line);
                        if (match.Success)
                        {
                            string highestRateStr = match.Groups[1].Value.Split(' ').Last().Trim('*');
                            if (double.TryParse(highestRateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double highestRateDouble))
                            {
                                highestSupportedRate = (int)Math.Max(highestSupportedRate, Math.Round(highestRateDouble));
                            }
                        }
                        else if (line == "HT capabilities:")
                        {
                            ht = true;
                        }
                        else if (line == "VHT capabilities:")
                        {
                            vht = true;
                        }
                        else
                        {
                            // SSID
                            match = SsidRegex.Match(line);
                            if (match.Success)
                            {
                                network.Name = match.Groups[1].Value;
                            }
                            else
                            {
                                // Channel
                                match = ChannelRegex.Match(line);
                                if (match.Success)
                                {
                                    network.Channel = int.Parse(match.Groups[1].Value);
                                }
                                else
                                {
                                    // Signal
                                    match = SignalRegex.Match(line);
                                    if (match.Success)
                                    {
                                        network.Rssi = int.Parse(match.Groups[1].Value);
                                    }
                                    else
                                    {
                                        // Auth
                                        if (line.StartsWith("RSN:"))
                                        {
                                            wpa2 = true;
                                        }
                                        else if (line.StartsWith("WPA:"))
                                        {
                                            wpa = true;
                                        }
                                        else if (line.StartsWith("* Authentication suites:"))
                                        {
                                            wpa3 = line.Contains("SAE");
                                            if (wpa3)
                                            {
                                                wpa2 = line.Contains("PSK");
                                            }
                                        }
                                        else if (WepRegex.IsMatch(line))
                                        {
                                            network.Auth = "WEP";
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (network is not null)
                {
                    AddNetwork();
                }
            }
            catch
            {
                _scanFailed = true;
            }
            _scanning = false;
        }

        public static Message GetResult(bool asJson)
        {
            if (asJson)
            {
                if (_scanning || _scanFailed || _networks is null)
                {
                    return new Message(MessageType.Success, "{\"err\":1}");
                }

                return new Message(MessageType.Success, JsonSerializer.Serialize(new
                {
                    networkScanResults = _networks.Select(network => new
                    {
                        ssid = network.Name,
                        chan = network.Channel,
                        rssi = network.Rssi,
                        phymode = network.PhyMode,
                        auth = network.Auth,
                        mac = network.MacAddress
                    }).ToArray(),
                    err = 0
                }));
            }

            if (_scanning)
            {
                return new Message(MessageType.Error, "scan is still in progress");
            }
            if (_scanFailed)
            {
                return new Message(MessageType.Error, "scan failed");
            }
            if (_networks is null)
            {
                return new Message(MessageType.Error, "no scan has been started");
            }
            
            StringBuilder result = new();
            foreach (var network in _networks)
            {
                result.AppendLine($"ssid={network.Name} chan={network.Channel} rssi={network.Rssi} phymode={network.PhyMode} auth={network.Auth} mac={network.MacAddress}");
            }
            return new Message(MessageType.Success, result.ToString().TrimEnd());
        }
    }
}
