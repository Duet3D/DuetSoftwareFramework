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
        private static readonly Regex _portRegex = new(@"^\s*Port\s*(\d+)");

        /// <summary>
        /// Initialize the protocol configuration
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Init()
        {
            if (File.Exists("/etc/proftpd/proftpd.conf"))
            {
                await using FileStream inetdConfig = new("/etc/proftpd/proftpd.conf", FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(inetdConfig);

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

                bool serviceEnabled = await Command.ExecQuery("systemctl", "is-enabled -q proftpd.service");
                await Manager.SetProtocol(NetworkProtocol.FTP, serviceEnabled);
            }
        }

        /// <summary>
        /// Configure the FTP server
        /// </summary>
        /// <param name="enabled">Enable FTP</param>
        /// <param name="port">Port</param>
        /// <returns>Configuration result</returns>
        public static async Task<Message> Configure(bool? enabled, int port)
        {
            // Don't proceed if no proftpd config is present
            if (!File.Exists("/etc/proftpd/proftpd.conf"))
            {
                return new Message(MessageType.Error, "Cannot configure ProFTPD because no configuration could be found");
            }

            // Update port
            bool portChanged = false;
            if (port > 0 && port != _port)
            {
                await using FileStream configStream = new("/etc/proftpd/proftpd.conf", FileMode.Open, FileAccess.ReadWrite);
                await using MemoryStream newConfigStream = new();
                using (StreamReader reader = new(configStream, leaveOpen: true))
                {
                    await using StreamWriter writer = new(newConfigStream, leaveOpen: true);

                    // Read the old config line by line and replace the Port argument
                    bool portWritten = false;
                    while (!reader.EndOfStream)
                    {
                        string line = await reader.ReadLineAsync();
                        if (!portWritten && _portRegex.IsMatch(line))
                        {
                            // Replace Port line with the new Port argument
                            await writer.WriteLineAsync($"Port\t\t\t\t{port}");
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
                        await writer.WriteLineAsync($"Port\t\t\t\t{port}");
                    }
                }

                // Overwrite the previous config
                configStream.Seek(0, SeekOrigin.Begin);
                configStream.SetLength(newConfigStream.Length);
                newConfigStream.Seek(0, SeekOrigin.Begin);
                await newConfigStream.CopyToAsync(configStream);

                // Assign new port
                _port = port;
                portChanged = true;
            }

            // Enable FTP
            if (enabled != null && enabled.Value && !Manager.IsEnabled(NetworkProtocol.FTP))
            {
                string startOutput = await Command.Execute("systemctl", "start proftpd.service");
                string enableOutput = await Command.Execute("systemctl", "enable -q proftpd.service");
                await Manager.SetProtocol(NetworkProtocol.FTP, true);
                return new Message(MessageType.Success, string.Join('\n', startOutput, enableOutput).Trim());
            }

            // Disable FTP
            if (enabled != null && !enabled.Value && Manager.IsEnabled(NetworkProtocol.FTP))
            {
                string stopOutput = await Command.Execute("systemctl", "stop proftpd.service");
                string disableOutput = await Command.Execute("systemctl", "disable -q proftpd.service");
                await Manager.SetProtocol(NetworkProtocol.FTP, false);
                return new Message(MessageType.Success, string.Join('\n', stopOutput, disableOutput).Trim());
            }

            // Restart FTP service
            if (portChanged)
            {
                string restartOutput = await Command.Execute("systemctl", "restart proftpd.service");
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
            if (Manager.IsEnabled(NetworkProtocol.FTP))
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
