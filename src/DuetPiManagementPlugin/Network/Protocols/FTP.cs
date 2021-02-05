using DuetAPI.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Protocol management for FTP
    /// </summary>
    public static class FTP
    {
        /// <summary>
        /// Main FTP port
        /// </summary>
        private static int _port = 21;

        /// <summary>
        /// Regex to capture the currently configured port
        /// </summary>
        private static Regex _portRegex = new Regex(@"^\s*Port\s*(\d+)");

        /// <summary>
        /// Initialize the protocol configuration
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Init()
        {
            if (File.Exists("/etc/proftpd/proftpd.conf") && await Command.ExecQuery("/usr/bin/systemctl", "is-enabled -q proftpd.service"))
            {
                using FileStream inetdConfig = new FileStream("/etc/proftpd.conf", FileMode.Open, FileAccess.Read);
                using StreamReader reader = new StreamReader(inetdConfig);

                while (!reader.EndOfStream)
                {
                    string line = await reader.ReadLineAsync();
                    Match match = _portRegex.Match(line);
                    if (match.Success)
                    {
                        _port = int.Parse(match.Groups[1].Value);
                        break;
                    }
                }
                await Manager.SetProtocol(NetworkProtocol.FTP, true);
            }
        }

        /// <summary>
        /// Configure the FTP server
        /// </summary>
        /// <param name="enabled">Enable FTP</param>
        /// <param name="port">Port</param>
        /// <returns>Configuration result</returns>
        public static async Task<Message> Configure(bool? enabled, int? port)
        {
            // Don't proceed if no proftpd config is present
            if (!File.Exists("/etc/proftpd/proftpd.conf"))
            {
                return new Message(MessageType.Error, "Cannot configure ProFTPD because no configuration could be found");
            }

            // Update port
            if (port != null && port != _port)
            {
                using FileStream configStream = new FileStream("/etc/proftpd/proftpd.conf", FileMode.Open, FileAccess.ReadWrite);
                using MemoryStream newConfigStream = new MemoryStream((int)configStream.Length);
                using (StreamReader reader = new StreamReader(configStream))
                {
                    using StreamWriter writer = new StreamWriter(newConfigStream);

                    // Read the old config line by line and replace the Port argument
                    bool portWritten = false;
                    while (!reader.EndOfStream)
                    {
                        string line = await reader.ReadLineAsync();
                        if (!portWritten && _portRegex.IsMatch(line))
                        {
                            // Replace Port line with the new Port argument
                            await writer.WriteLineAsync($"Port\t\t\t\t{_port}");
                            portWritten = true;
                        }
                        else
                        {
                            // Copy other contents
                            await writer.WriteLineAsync(line);
                        }
                    }

                    if (!portWritten)
                    {
                        // Port not written yet, should not happen
                        await writer.WriteLineAsync($"Port\t\t\t\t{_port}");
                    }
                }

                // Overwrite the previous config
                configStream.Seek(0, SeekOrigin.Begin);
                configStream.SetLength(newConfigStream.Length);
                await newConfigStream.CopyToAsync(configStream);

                // Assign new port
                _port = port.Value;
            }

            // Enable FTP
            if (enabled.Value && !Manager.EnabledProtocols.Contains(NetworkProtocol.FTP))
            {
                string startOutput = await Command.Execute("/usr/bin/systemctl", "start proftpd.service");
                string enableOutput = await Command.Execute("/usr/bin/systemctl", "enable proftpd.service");
                await Manager.SetProtocol(NetworkProtocol.FTP, true);
                return new Message(MessageType.Success, string.Join('\n', startOutput.TrimEnd(), enableOutput).TrimEnd());
            }

            // Disable FTP
            if (!enabled.Value && Manager.EnabledProtocols.Contains(NetworkProtocol.FTP))
            {
                string stopOutput = await Command.Execute("/usr/bin/systemctl", "stop proftpd.service");
                string disableOutput = await Command.Execute("/usr/bin/systemctl", "disable proftpd.service");
                await Manager.SetProtocol(NetworkProtocol.FTP, false);
                return new Message(MessageType.Success, string.Join('\n', stopOutput.TrimEnd(), disableOutput).TrimEnd());
            }

            // Restart FTP service
            if (port != null && port != _port)
            {
                string restartOutput = await Command.Execute("/usr/bin/systemctl", "restart proftpd.service");
                return new Message(MessageType.Success, restartOutput);
            }

            // Don't do anything
            return new Message();
        }

        /// <summary>
        /// Report the current state of the FTP protocol
        /// </summary>
        /// <param name="builder">String builder</param>
        public static void Report(StringBuilder builder)
        {
            if (Manager.EnabledProtocols.Contains(NetworkProtocol.FTP))
            {
                builder.AppendLine($"FTP is enabled on port {_port}");
            }
            else
            {
                builder.AppendLine("FTP is disabled");
            }
        }
    }
}
