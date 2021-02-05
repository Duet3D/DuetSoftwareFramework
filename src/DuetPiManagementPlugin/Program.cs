using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// Main class of this program.
    /// Note it is tailored for DuetPi, so most of the file paths etc. are hard-coded (for now)
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Version of this application
        /// </summary>
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        /// <summary>
        /// List of codes to intercept
        /// </summary>
        public static readonly string[] CodesToIntercept =
        {
            "M540",     // Set MAC address
            "M550",     // Set Name
            "M552",     // Set IP address, enable/disable network interface
            "M553",     // Set Netmask
            "M554",     // Set Gateway
            "M586",     // Configure network protocols
            "M587",     // Add WiFi host network to remembered list, or list remembered networks
            "M588",     // Forget WiFi host network
            "M589",     // Configure access point parameters
            "M999"      // Reboot SBC (priority codes are not handled)
        };

        /// <summary>
        /// Connection used for intecepting codes
        /// </summary>
        public static InterceptConnection Connection { get; private set; } = new InterceptConnection();

        /// <summary>
        /// Global cancellation source that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationTokenSource CancelSource = new CancellationTokenSource();

        /// <summary>
        /// Global cancellation token that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationToken CancellationToken = CancelSource.Token;

        /// <summary>
        /// Entry point of this application
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        static async Task Main(string[] args)
        {
            string socketPath = Defaults.FullSocketPath;

            // Parse command line parameters
            string lastArg = null;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else
                {
                    lastArg = arg;
                }
            }

            // Create an intercepting connection for codes that are not supported natively by DCS
            using InterceptConnection connection = new InterceptConnection();
            await connection.Connect(InterceptionMode.Pre, null, CodesToIntercept, false, socketPath);

            // Deal with program termination requests (SIGTERM and Ctrl+C)
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    CancelSource.Cancel();
                }
            };
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    e.Cancel = true;
                    CancelSource.Cancel();
                }
            };

            // Read the network settings
            await Network.Protocols.Manager.Init();

            // Keep intercepting codes until the plugin is stopped
            do
            {
                Code code = await connection.ReceiveCode(CancellationToken);
                switch (code.MajorNumber)
                {
                    // Set MAC address
                    case 540:
                        if (await connection.Flush(CancellationToken))
                        {
                            int index = code.Parameter('I', 0);
                            try
                            {
                                if (code.Parameter('P') != null)
                                {
                                    string ifaceName = Network.Interface.Get(index).Name;
                                    Message setResult = await Network.Interface.SetMACAddress(ifaceName, code.Parameter('P'));
                                    await connection.ResolveCode(setResult, CancellationToken);
                                }
                                else
                                {
                                    byte[] macAddress = Network.Interface.Get(index).GetPhysicalAddress().GetAddressBytes();
                                    await connection.ResolveCode(MessageType.Success, $"MAC: {BitConverter.ToString(macAddress).Replace('-', ':')}", CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set hostname
                    case 550:
                        if (await connection.Flush(CancellationToken))
                        {
                            string newHostname = code.Parameter('P', string.Empty);
                            if (!string.IsNullOrWhiteSpace(newHostname))
                            {
                                if (newHostname.Length > 40)
                                {
                                    await connection.ResolveCode(MessageType.Error, "Machine name is too long", CancellationToken);
                                }
                                else if (newHostname.Contains('\"') || newHostname.Contains('\\'))
                                {
                                    await connection.ResolveCode(MessageType.Error, "Hostname contains invalid characters", CancellationToken);
                                }
                                else
                                {
                                    string setResult = await Command.Execute("/usr/bin/hostnamectl", $"set-hostname \"{newHostname}\"");
                                    if (string.IsNullOrWhiteSpace(setResult))
                                    {
                                        // Success, let DSF/RRF process this code too
                                        await connection.IgnoreCode(CancellationToken);
                                    }
                                    else
                                    {
                                        // Something is not right
                                        await connection.ResolveCode(MessageType.Success, setResult, CancellationToken);
                                    }
                                }
                            }
                            else
                            {
                                // Let RRF generate the response
                                await connection.IgnoreCode(CancellationToken);
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set IP address, enable/disable network interface
                    case 552:
                        if (await connection.Flush(CancellationToken))
                        {
                            CodeParameter pParam = code.Parameter('P');
                            CodeParameter sParam = code.Parameter('S');
                            if (code.Parameter('I') == null && pParam == null && sParam == null)
                            {
                                StringBuilder builder = new StringBuilder();
                                await Network.Interface.Report(builder);
                                await connection.ResolveCode(MessageType.Success, builder.ToString().TrimEnd(), CancellationToken);
                            }
                            else
                            {
                                int index = code.Parameter('I', 0);
                                try
                                {
                                    Message manageResult = await Network.Interface.SetConfig(index, pParam, sParam);
                                    await connection.ResolveCode(manageResult, CancellationToken);
                                }
                                catch (Exception e)
                                {
                                    await connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                }
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set Netmask
                    case 553:
                        if (await connection.Flush(CancellationToken))
                        {
                            int index = code.Parameter('I', 0);
                            string result = await Network.Interface.SetNetmask(index, code.Parameter('P'));
                            await connection.ResolveCode(MessageType.Success, result, CancellationToken);
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set Gateway and/or DNS server
                    case 554:
                        if (await connection.Flush(CancellationToken))
                        {
                            int index = code.Parameter('I', 0);
                            string result = await Network.Interface.SetGateway(index, code.Parameter('P'), code.Parameter('S'));
                            await connection.ResolveCode(MessageType.Success, result, CancellationToken);
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Configure network protocols
                    case 586:
                        if (await connection.Flush(CancellationToken))
                        {
                            try
                            {
                                if (code.Parameter('P') != null)
                                {
                                    int protocol = code.Parameter('P', 0), tls = code.Parameter('T', 0);
                                    CodeParameter enabled = code.Parameter('S'), port = code.Parameter('R');
                                    Message result = await Network.Protocols.Manager.ConfigureProtocols(protocol, (enabled != null) ? (enabled > 0) : null, tls > 0, port);
                                    if (string.IsNullOrWhiteSpace(result.Content) && code.Parameter('C') != null)
                                    {
                                        // Let DSF/RRF process the combined C parameter
                                        await connection.IgnoreCode(CancellationToken);
                                    }
                                    else
                                    {
                                        // Return the result of this action
                                        await connection.ResolveCode(result, CancellationToken);
                                    }
                                }
                                else if (code.Parameter('C') != null)
                                {
                                    // Let DSF/RRF process the standalone C parameter
                                    await connection.IgnoreCode(CancellationToken);
                                }
                                else
                                {
                                    // Report the protocol status
                                    Message report = await Network.Protocols.Manager.ReportProtocols();
                                    await connection.ResolveCode(report, CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Add WiFi host network to remembered list, or list remembered networks
                    case 587:
                        if (await connection.Flush(CancellationToken))
                        {
                            try
                            {
                                string ssid = code.Parameter('S');
                                string psk = code.Parameter('P');
                                if (psk != null)
                                {
                                    if (ssid == null)
                                    {
                                        await connection.ResolveCode(MessageType.Error, "Missing S parameter");
                                        break;
                                    }
                                    if (psk.Length < 8 || psk.Length > 64)
                                    {
                                        await connection.ResolveCode(MessageType.Error, "WiFi password must be between 8 and 64 characters");
                                        break;
                                    }
                                }
                                string countryCode = code.Parameter('C');
                                IPAddress ip = code.Parameter('I');
                                IPAddress gateway = code.Parameter('J');
                                IPAddress netmask = code.Parameter('K');
                                IPAddress dnsServer = code.Parameter('L');

                                if ((ssid == null || psk == null) && countryCode != null)
                                {
                                    // Output currently configured SSIDs
                                    Message ssidReport = await Network.WPA.Report();
                                    await connection.ResolveCode(ssidReport, CancellationToken);
                                }
                                else
                                {
                                    // Update SSID/PSK and/or country code
                                    Message configResult = await Network.WPA.UpdateSSID(ssid, psk, countryCode);
                                    if (configResult.Type == MessageType.Success)
                                    {
                                        // Update IP configuration as well if needed
                                        string setIPResult = await Network.WPA.SetIPAddress(ip, netmask, gateway, dnsServer);
                                        configResult.Content = (configResult.Content + '\n' + setIPResult).Trim();
                                    }
                                    await connection.ResolveCode(configResult, CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Forget WiFi host network
                    case 588:
                        if (await connection.Flush(CancellationToken))
                        {
                            try
                            {
                                string ssid = code.Parameter('S');
                                if (ssid == null)
                                {
                                    await connection.ResolveCode(MessageType.Error, "Missing S parameter", CancellationToken);
                                }
                                else
                                {
                                    // Remove SSID(s) if possible
                                    Message configResult = await Network.WPA.UpdateSSID(ssid, null);
                                    await connection.ResolveCode(configResult, CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Configure access point parameters
                    case 589:
                        if (await connection.Flush(CancellationToken))
                        {
                            try
                            {
                                string ssid = code.Parameter('S');
                                if (string.IsNullOrWhiteSpace(ssid))
                                {
                                    await connection.ResolveCode(MessageType.Error, "Missing S parameter");
                                    break;
                                }
                                string password = code.Parameter('P');
                                if (string.IsNullOrWhiteSpace(password))
                                {
                                    await connection.ResolveCode(MessageType.Error, "Missing P parameter");
                                    break;
                                }
                                IPAddress ipAddress = code.Parameter('I');
                                if (ipAddress == null)
                                {
                                    await connection.ResolveCode(MessageType.Error, "Missing I parameter");
                                }
                                int channel = code.Parameter('C', 6);

                                // Set up hostapd configuration
                                Message configResult = await Network.AccessPoint.Configure(ssid, password, ipAddress, channel);
                            }
                            catch (Exception e)
                            {
                                await connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                            }
                        }
                        else
                        {
                            await connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Reboot SBC
                    case 999:
                        if (code.Parameter('B', 0) == -1)
                        {
                            if (await connection.Flush(CancellationToken))
                            {
                                string rebootResult = await Command.Execute("/usr/bin/systemctl", "reboot");
                                await connection.ResolveCode(new Message(MessageType.Success, rebootResult));
                            }
                            else
                            {
                                await connection.CancelCode(CancellationToken);
                            }
                        }
                        break;
                }
            }
            while (!CancellationToken.IsCancellationRequested);
        }
    }
}
