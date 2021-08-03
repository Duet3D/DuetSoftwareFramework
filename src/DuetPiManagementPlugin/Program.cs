using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
using System.IO;
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
            "M21",      // Initialize SD card
            "M22",      // Release SD card
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
        public static InterceptConnection Connection { get; } = new();

        /// <summary>
        /// Global cancellation source that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationTokenSource CancelSource = new();

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
            await Connection.Connect(InterceptionMode.Pre, null, CodesToIntercept, false, socketPath);

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
                Code code;
                try
                {
                    code = await Connection.ReceiveCode(CancellationToken);
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {
                        throw;
                    }
                    break;
                }

                if (code.Type != CodeType.MCode)
                {
                    // We're only interested in M-codes...
                    continue;
                }

                switch (code.MajorNumber)
                {
                    // Initialize SD card
                    case 21:
                        if (code.Parameter('P').Type == typeof(string))
                        {
                            if (await Connection.Flush(CancellationToken))
                            {
                                string device = code.Parameter('P'), directory = code.Parameter('S');
                                string type = code.Parameter('T'), options = code.Parameter('O');
                                try
                                {
                                    if (!string.IsNullOrEmpty(directory))
                                    {
                                        directory = await Connection.ResolvePath(directory, CancellationToken);
                                    }
                                    Message result = await Mount.MountShare(device, directory, type, options);
                                    await Connection.ResolveCode(result, CancellationToken);
                                }
                                catch (Exception e)
                                {
                                    await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                    Console.WriteLine(e);
                                }
                            }
                            else
                            {
                                await Connection.CancelCode();
                            }
                        }
                        else
                        {
                            await Connection.IgnoreCode();
                        }
                        break;

                    // Release SD card
                    case 22:
                        if (code.Parameter('P').Type == typeof(string))
                        {
                            if (await Connection.Flush(CancellationToken))
                            {
                                string node = code.Parameter('P');
                                try
                                {
                                    string directory = await Connection.ResolvePath(node, CancellationToken);
                                    if (Directory.Exists(directory))
                                    {
                                        node = directory;
                                    }

                                    Message result = await Mount.UnmountShare(node);
                                    await Connection.ResolveCode(result, CancellationToken);
                                }
                                catch (Exception e)
                                {
                                    await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                    Console.WriteLine(e);
                                }
                            }
                            else
                            {
                                await Connection.CancelCode();
                            }
                        }
                        else
                        {
                            await Connection.IgnoreCode();
                        }
                        break;

                    // Set MAC address
                    case 540:
                        if (await Connection.Flush(CancellationToken))
                        {
                            int index = code.Parameter('I', 0);
                            try
                            {
                                if (code.Parameter('P') != null)
                                {
                                    Message setResult = await Network.Interface.SetMACAddress(index, code.Parameter('P'));
                                    await Connection.ResolveCode(setResult, CancellationToken);
                                }
                                else
                                {
                                    byte[] macAddress = Network.Interface.Get(index).GetPhysicalAddress().GetAddressBytes();
                                    await Connection.ResolveCode(MessageType.Success, $"MAC: {BitConverter.ToString(macAddress).Replace('-', ':')}", CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                Console.WriteLine(e);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set hostname
                    case 550:
                        if (await Connection.Flush(CancellationToken))
                        {
                            string newHostname = code.Parameter('P', string.Empty);
                            if (!string.IsNullOrWhiteSpace(newHostname))
                            {
                                if (newHostname.Length > 40)
                                {
                                    await Connection.ResolveCode(MessageType.Error, "Machine name is too long", CancellationToken);
                                }
                                else if (newHostname.Contains('\"') || newHostname.Contains('\\'))
                                {
                                    await Connection.ResolveCode(MessageType.Error, "Hostname contains invalid characters", CancellationToken);
                                }
                                else
                                {
                                    string setResult = await Command.Execute("/usr/bin/hostnamectl", $"set-hostname \"{newHostname}\"");
                                    if (string.IsNullOrWhiteSpace(setResult))
                                    {
                                        // Success, let DSF/RRF process this code too
                                        await Connection.IgnoreCode(CancellationToken);
                                    }
                                    else
                                    {
                                        // Something is not right
                                        await Connection.ResolveCode(MessageType.Success, setResult, CancellationToken);
                                    }
                                }
                            }
                            else
                            {
                                // Let RRF generate the response
                                await Connection.IgnoreCode(CancellationToken);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set IP address, enable/disable network interface
                    case 552:
                        if (await Connection.Flush(CancellationToken))
                        {
                            CodeParameter pParam = code.Parameter('P');
                            CodeParameter sParam = code.Parameter('S');
                            if (pParam == null && sParam == null)
                            {
                                StringBuilder builder = new();
                                await Network.Interface.Report(builder, null, code.Parameter('I', -1));
                                await Connection.ResolveCode(MessageType.Success, builder.ToString().TrimEnd(), CancellationToken);
                            }
                            else
                            {
                                int index = code.Parameter('I', 0);
                                try
                                {
                                    Message manageResult = await Network.Interface.SetConfig(index, pParam, sParam);
                                    await Connection.ResolveCode(manageResult, CancellationToken);
                                }
                                catch (Exception e)
                                {
                                    await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                    Console.WriteLine(e);
                                }
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set Netmask
                    case 553:
                        if (await Connection.Flush(CancellationToken))
                        {
                            int index = code.Parameter('I', 0);
                            try
                            {
                                string result = await Network.Interface.ManageNetmask(index, code.Parameter('P'));
                                await Connection.ResolveCode(MessageType.Success, result, CancellationToken);
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                Console.WriteLine(e);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Set Gateway and/or DNS server
                    case 554:
                        if (await Connection.Flush(CancellationToken))
                        {
                            int index = code.Parameter('I', 0);
                            string result = await Network.Interface.ManageGateway(index, code.Parameter('P'), code.Parameter('S'));
                            await Connection.ResolveCode(MessageType.Success, result, CancellationToken);
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Configure network protocols
                    case 586:
                        if (await Connection.Flush(CancellationToken))
                        {
                            try
                            {
                                if (code.Parameter('P') != null)
                                {
                                    int protocol = code.Parameter('P', 0), tls = code.Parameter('T', 0), port = code.Parameter('R', 0);
                                    CodeParameter enabled = code.Parameter('S');
                                    Message result = await Network.Protocols.Manager.ConfigureProtocols(protocol, (enabled != null) ? (enabled > 0) : null, tls > 0, port);
                                    if (string.IsNullOrWhiteSpace(result.Content) && code.Parameter('C') != null)
                                    {
                                        // Let DSF/RRF process the combined C parameter
                                        await Connection.IgnoreCode(CancellationToken);
                                    }
                                    else
                                    {
                                        // Return the result of this action
                                        await Connection.ResolveCode(result, CancellationToken);
                                    }
                                }
                                else if (code.Parameter('C') != null)
                                {
                                    // Let DSF/RRF process the standalone C parameter
                                    await Connection.IgnoreCode(CancellationToken);
                                }
                                else
                                {
                                    // Report the protocol status
                                    Message report = await Network.Protocols.Manager.ReportProtocols();
                                    await Connection.ResolveCode(report, CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Add WiFi host network to remembered list, or list remembered networks
                    case 587:
                        if (await Connection.Flush(CancellationToken))
                        {
                            try
                            {
                                string ssid = code.Parameter('S');
                                string psk = code.Parameter('P');
                                if (psk != null)
                                {
                                    if (ssid == null)
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "Missing S parameter");
                                        break;
                                    }
                                    if (psk.Length < 8 || psk.Length > 64)
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "WiFi password must be between 8 and 64 characters");
                                        break;
                                    }
                                }
                                string countryCode = code.Parameter('C');
                                IPAddress ip = code.Parameter('I');
                                IPAddress gateway = code.Parameter('J');
                                IPAddress netmask = code.Parameter('K');
                                IPAddress dnsServer = code.Parameter('L');

                                if ((ssid == null || psk == null) && countryCode == null)
                                {
                                    // Output currently configured SSIDs
                                    Message ssidReport = await Network.WPA.Report();
                                    await Connection.ResolveCode(ssidReport, CancellationToken);
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
                                    await Connection.ResolveCode(configResult, CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Forget WiFi host network
                    case 588:
                        if (await Connection.Flush(CancellationToken))
                        {
                            try
                            {
                                string ssid = code.Parameter('S');
                                if (ssid == null)
                                {
                                    await Connection.ResolveCode(MessageType.Error, "Missing S parameter", CancellationToken);
                                }
                                else
                                {
                                    // Remove SSID(s) if possible
                                    Message configResult = await Network.WPA.UpdateSSID(ssid, null);
                                    await Connection.ResolveCode(configResult, CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Configure access point parameters
                    case 589:
                        if (await Connection.Flush(CancellationToken))
                        {
                            try
                            {
                                string ssid = code.Parameter('S');
                                if (string.IsNullOrWhiteSpace(ssid))
                                {
                                    await Connection.ResolveCode(MessageType.Error, "Missing S parameter");
                                    break;
                                }
                                string password = code.Parameter('P');
                                if (string.IsNullOrWhiteSpace(password))
                                {
                                    await Connection.ResolveCode(MessageType.Error, "Missing P parameter");
                                    break;
                                }
                                IPAddress ipAddress = code.Parameter('I');
                                if (ipAddress == null)
                                {
                                    await Connection.ResolveCode(MessageType.Error, "Missing I parameter");
                                }
                                int channel = code.Parameter('C', 6);

                                // Set up hostapd configuration
                                Message configResult = await Network.AccessPoint.Configure(ssid, password, ipAddress, channel);
                                await Connection.ResolveCode(configResult, CancellationToken);
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                        }
                        else
                        {
                            await Connection.CancelCode(CancellationToken);
                        }
                        break;

                    // Reboot SBC
                    case 999:
                        if (code.Parameter('B', 0) == -1)
                        {
                            if (await Connection.Flush(CancellationToken))
                            {
                                string rebootResult = await Command.Execute("/usr/bin/systemctl", "reboot");
                                await Connection.ResolveCode(MessageType.Success, rebootResult);
                            }
                            else
                            {
                                await Connection.CancelCode(CancellationToken);
                            }
                        }
                        break;
                }
            }
            while (!CancellationToken.IsCancellationRequested);
        }
    }
}
