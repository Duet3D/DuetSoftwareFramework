using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetPiManagementPlugin.Network;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

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
            "M587.1",   // Start WiFi scan
            "M587.2",   // List WiFi scan results
            "M588",     // Forget WiFi host network
            "M589",     // Configure access point parameters
            "M905",     // Set current RTC date and time
            "M997",     // Perform update
            "M999"      // Reboot or shutdown SBC (priority codes are not handled)
        };

        /// <summary>
        /// Connection used for intercepting codes
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
            string? lastArg = null;
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

            // Keep the WiFi country up-to-date
            await CountryCodeUpdater.Init(socketPath);

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

                    // Don't process system codes that need to go straight to the firmware
                    if (code.Flags.HasFlag(CodeFlags.IsInternallyProcessed))
                    {
                        await Connection.IgnoreCode();
                        continue;
                    }
                }
                catch (Exception e) when (e is OperationCanceledException)
                {
                    // Plugin is supposed to be terminated, stop here
                    break;
                }

                try
                {
                    switch (code.MajorNumber)
                    {
                        // Initialize SD card
                        case 21:
                            {
                                if (code.TryGetParameter('P', out CodeParameter? pParam))
                                {
                                    if (pParam.Type == typeof(string))
                                    {
                                        string device = (string)pParam;
                                        string? directory = code.GetOptionalString('S'), type = code.GetOptionalString('T'), options = code.GetOptionalString('O');
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
                                    else if (pParam.Type == typeof(int))
                                    {
                                        // PanelDue wants to mount an already mounted volume, handle it
                                        await Connection.ResolveCode(MessageType.Success, string.Empty, CancellationToken);
                                    }
                                    else
                                    {
                                        // Unsupported P parameter
                                        await Connection.ResolveCode(MessageType.Error, "Unsupported P parameter", CancellationToken);
                                    }
                                }
                                else
                                {
                                    await Connection.IgnoreCode();
                                }
                                break;
                            }

                        // Release SD card
                        case 22:
                            if (code.TryGetString('P', out string? node))
                            {
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
                                await Connection.IgnoreCode();
                            }
                            break;

                        // Set MAC address
                        case 540:
                            try
                            {
                                int index = code.GetInt('I', 0);
                                if (code.TryGetString('P', out string? address))
                                {
                                    Message setResult = await Interface.SetMACAddress(index, address);
                                    await Connection.ResolveCode(setResult, CancellationToken);
                                }
                                else
                                {
                                    byte[] macAddress = Interface.Get(index).GetPhysicalAddress().GetAddressBytes();
                                    await Connection.ResolveCode(MessageType.Success, $"MAC: {BitConverter.ToString(macAddress).Replace('-', ':')}", CancellationToken);
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Set hostname
                        case 550:
                            if (code.TryGetString('P', out string? newHostname))
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
                                    Regex hostnameRegex = new(@"^\s*127\.0\.[01]\.1\s+" + Environment.MachineName + @"\s*$", RegexOptions.IgnoreCase);

                                    // 1. Apply new hostname using hostnamectl
                                    string setResult = await Command.Execute("hostnamectl", $"set-hostname \"{newHostname}\"");
                                    if (!string.IsNullOrWhiteSpace(setResult))
                                    {
                                        await Connection.ResolveCode(MessageType.Error, setResult, CancellationToken);
                                        break;
                                    }

                                    // 2. Update hostname in /etc/hosts
                                    bool hostnameWritten = false;
                                    string[] hostsFile = await File.ReadAllLinesAsync("/etc/hosts");
                                    await using (FileStream fs = new("/etc/hosts", FileMode.Create, FileAccess.Write))
                                    {
                                        using StreamWriter writer = new(fs);
                                        foreach (string line in hostsFile)
                                        {
                                            if (!hostnameWritten && hostnameRegex.Match(line).Success)
                                            {
                                                await writer.WriteLineAsync("127.0.1.1       " + Environment.MachineName);
                                                hostnameWritten = true;
                                            }
                                            else
                                            {
                                                await writer.WriteLineAsync(line);
                                            }
                                        }

                                        if (!hostnameWritten)
                                        {
                                            await writer.WriteLineAsync("127.0.1.1       " + Environment.MachineName);
                                        }
                                    }

                                    // Success, let DSF/RRF process this code too
                                    await Connection.IgnoreCode(CancellationToken);
                                }
                            }
                            else
                            {
                                // Let RRF generate the response
                                await Connection.IgnoreCode(CancellationToken);
                            }
                            break;

                        // Set IP address, enable/disable network interface
                        case 552:
                            {
                                bool hasPParam = code.TryGetString('P', out string? pParam), hasSParam = code.TryGetInt('S', out int? sParam);
                                if (hasPParam || hasSParam)
                                {
                                    int index = code.GetInt('I', 0);
                                    try
                                    {
                                        Message manageResult = await Interface.SetConfig(index, pParam, sParam);
                                        await Connection.ResolveCode(manageResult, CancellationToken);
                                    }
                                    catch (Exception e)
                                    {
                                        await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                        Console.WriteLine(e);
                                    }
                                }
                                else
                                {
                                    StringBuilder builder = new();
                                    await Interface.Report(builder, null, code.GetInt('I', -1));
                                    await Connection.ResolveCode(MessageType.Success, builder.ToString().TrimEnd(), CancellationToken);
                                }
                            }
                            break;

                        // Set Netmask
                        case 553:
                            try
                            {
                                int index = code.GetInt('I', 0);
                                string result = await Interface.ManageNetmask(index, code.GetIPAddress('P'));
                                await Connection.ResolveCode(MessageType.Success, result, CancellationToken);
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Set Gateway and/or DNS server
                        case 554:
                            try
                            {
                                int index = code.GetInt('I', 0);
                                _ = code.TryGetIPAddress('P', out IPAddress? gateway);
                                _ = code.TryGetIPAddress('S', out IPAddress? dnsServer);
                                string result = await Interface.ManageGateway(index, gateway, dnsServer);
                                await Connection.ResolveCode(MessageType.Success, result, CancellationToken);
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, e.Message, CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Configure network protocols
                        case 586:
                            try
                            {
                                if (code.TryGetInt('P', out int protocol))
                                {
                                    code.TryGetBool('S', out bool? enabled);
                                    Message result = await Network.Protocols.Manager.ConfigureProtocols(protocol, enabled, code.GetBool('T', false), code.GetInt('R', 0));
                                    if (string.IsNullOrWhiteSpace(result.Content) && code.HasParameter('C'))
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
                                else if (code.HasParameter('C'))
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
                            break;

                        // M587: Add WiFi host network to remembered list, or list remembered networks
                        // M587.1: Start WiFi scan
                        // M587.2: List WiFi scan results
                        case 587:
                            try
                            {
                                if (code.MinorNumber is null || code.MinorNumber == 0)
                                {
                                    code.TryGetString('S', out string? ssid);
                                    if (code.TryGetString('P', out string? psk))
                                    {
                                        if (ssid is null)
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
                                    code.TryGetString('C', out string? countryCode);
                                    code.TryGetIPAddress('I', out IPAddress? ip);
                                    code.TryGetIPAddress('J', out IPAddress? gateway);
                                    code.TryGetIPAddress('K', out IPAddress? netmask);
                                    code.TryGetIPAddress('L', out IPAddress? dnsServer);

                                    if (ssid is null && countryCode is null)
                                    {
                                        // Output currently configured SSIDs
                                        Message ssidReport = await Interface.ReportSSIDs();
                                        await Connection.ResolveCode(ssidReport, CancellationToken);
                                    }
                                    else
                                    {
                                        // Update SSID/PSK and/or country code
                                        Message configResult = await Interface.UpdateSSID(ssid, psk, countryCode);
                                        if (configResult.Type == MessageType.Success)
                                        {
                                            // Update IP configuration as well if needed
                                            string setIPResult = await Interface.SetIPAddress("wlan0", ip, netmask, gateway, dnsServer);
                                            configResult.Content = (configResult.Content + '\n' + setIPResult).Trim();
                                        }
                                        await Connection.ResolveCode(configResult, CancellationToken);
                                    }
                                }
                                else if (code.MinorNumber == 1)
                                {
                                    Message startResult = WifiScan.Start();
                                    await Connection.ResolveCode(startResult, CancellationToken);
                                }
                                else if (code.MinorNumber == 2)
                                {
                                    Message scanResult = WifiScan.GetResult(code.GetBool('F', false));
                                    await Connection.ResolveCode(scanResult, CancellationToken);
                                }
                                else
                                {
                                    await Connection.ResolveCode(MessageType.Warning, "Command is not supported");
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Forget WiFi host network
                        case 588:
                            try
                            {
                                // Remove SSID(s) if possible
                                Message configResult = await Interface.UpdateSSID(code.GetString('S'), null);
                                await Connection.ResolveCode(configResult, CancellationToken);
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Configure access point parameters
                        case 589:
                            try
                            {
                                // Set up hostapd configuration
                                Message configResult = await AccessPoint.Configure(code.GetString('S'), code.GetString('P'), code.GetIPAddress('I', IPAddress.Any), code.GetInt('C', 6));
                                await Connection.ResolveCode(configResult, CancellationToken);
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Set current RTC date and time
                        case 905:
                            try
                            {
                                bool seen = false;

                                if (code.TryGetBool('A', out bool? useNTP))
                                {
                                    if (!await Command.ExecQuery("timedatectl", $"set-ntp {(useNTP.Value ? "true" : "false")}"))
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "Failed to set NTP");
                                        break;
                                    }
                                    seen = true;
                                }

                                if (code.TryGetString('P', out string? dayString))
                                {
                                    if (DateTime.TryParseExact(dayString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                                    {
                                        if (!await Command.ExecQuery("timedatectl", $"set-time {date:yyyy-MM-dd}"))
                                        {
                                            await Connection.ResolveCode(MessageType.Error, "Failed to set date (NTP enabled?)");
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "Invalid date format");
                                        break;
                                    }
                                }

                                if (code.TryGetString('S', out string? timeString))
                                {
                                    if (DateTime.TryParseExact(timeString, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
                                    {
                                        if (!await Command.ExecQuery("timedatectl", $"set-time {time:HH:mm:ss}"))
                                        {
                                            await Connection.ResolveCode(MessageType.Error, "Failed to set time (NYP enabled?)");
                                            break;
                                        }
                                        seen = true;
                                    }
                                    else
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "Invalid time format");
                                        break;
                                    }
                                }

                                if (code.TryGetString('T', out string? timezone))
                                {
                                    if (File.Exists($"/usr/share/zoneinfo/{timezone}"))
                                    {
                                        await Process.Start("timedatectl", $"set-timezone {timezone}").WaitForExitAsync(CancellationToken);
                                        seen = true;
                                    }
                                    else
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "Invalid time zone");
                                        break;
                                    }
                                }

                                if (seen)
                                {
                                    await Connection.IgnoreCode();      // RRF needs to see M905 as well
                                }
                                else
                                {
                                    await Connection.ResolveCode(MessageType.Success, $"Current date and time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                                }
                            }
                            catch (Exception e)
                            {
                                await Connection.ResolveCode(MessageType.Error, $"Failed to perform action: {e.Message}", CancellationToken);
                                Console.WriteLine(e);
                            }
                            break;

                        // Perform update
                        case 997:
                            if (code.GetInt('S', 0) == 2)
                            {
                                // Check if we need to change the package feed
                                if (code.TryGetString('F', out string? packageFeed))
                                {
                                    if (!Regex.IsMatch(packageFeed, @"^(un)?stable(-\d+\.\d+)?$") && packageFeed != "dev")
                                    {
                                        await Connection.ResolveCode(MessageType.Error, "Invalid package feed");
                                        break;
                                    }

                                    try
                                    {
                                        // Delete obsolete package lists
                                        if (File.Exists("/etc/apt/sources.list.d/duet3d-unstable.list"))
                                        {
                                            File.Delete("/etc/apt/sources.list.d/duet3d-unstable.list");
                                        }

                                        // Rewrite duet3d.list
                                        await File.WriteAllTextAsync("/etc/apt/sources.list.d/duet3d.list", $"deb https://pkg.duet3d.com/ {packageFeed} armv7");

                                        // Done
                                        await Connection.ResolveCode(MessageType.Success, string.Empty);
                                        break;
                                    }
                                    catch (Exception e)
                                    {
                                        await Connection.ResolveCode(MessageType.Error, $"Failed to perform update: {e.Message}", CancelSource.Token);
                                    }
                                }

                                try
                                {
                                    // Put DSF into update status
                                    await Connection.SetUpdateStatus(true);

                                    // Update package lists
                                    using (Process updateProcess = Process.Start("/usr/bin/apt-get", "-y update"))
                                    {
                                        await updateProcess.WaitForExitAsync(CancelSource.Token);
                                        if (updateProcess.ExitCode != 0)
                                        {
                                            throw new Exception("Update process return non-zero exit code");
                                        }
                                    }

                                    string result = string.Empty;
                                    if (code.TryGetString('V', out string? version))
                                    {
                                        // Install specific DSF/RRF version
                                        result = await Command.Execute("install-dsf.sh", version);
                                    }
                                    else
                                    {
                                        // Perform upgrade
                                        await Command.ExecQuery("/usr/bin/unattended-upgrade", string.Empty);
                                    }

                                    // Done
                                    await Connection.SetUpdateStatus(false, CancelSource.Token);
                                    await Connection.ResolveCode(string.IsNullOrEmpty(result) ? MessageType.Success : MessageType.Error, result, CancelSource.Token);
                                }
                                catch (OperationCanceledException)
                                {
                                    // Plugin is being updated, attempt to resolve the code at last
                                    await Connection.ResolveCode(MessageType.Success, string.Empty);
                                }
                                catch (Exception e)
                                {
                                    // Something went wrong
                                    await Connection.SetUpdateStatus(false, CancelSource.Token);
                                    await Connection.ResolveCode(MessageType.Error, $"Failed to perform update: {e.Message}", CancelSource.Token);
                                }
                            }
                            else
                            {
                                await Connection.IgnoreCode(CancellationToken);
                            }
                            break;

                        // Reboot or shut down SBC
                        case 999:
                            if (code.GetInt('B', 0) == -1)
                            {
                                string rebootResult = await Command.Execute("systemctl", (code.GetString('P', string.Empty) == "OFF") ? "poweroff" : "reboot");
                                await Connection.ResolveCode(MessageType.Success, rebootResult);
                            }
                            else
                            {
                                await Connection.IgnoreCode(CancellationToken);
                            }
                            break;

                        // Unknown code. Should never get here
                        default:
                            await Connection.IgnoreCode();
                            break;
                    }
                }
                catch (Exception e) when (e is MissingParameterException or InvalidParameterTypeException)
                {
                    await Connection.ResolveCode(MessageType.Error, $"{code.ToShortString()}: {e.Message}");
                }
            }
            while (!CancellationToken.IsCancellationRequested);
        }
    }
}
