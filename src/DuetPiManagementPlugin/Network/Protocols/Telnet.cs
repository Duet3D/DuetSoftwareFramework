using DuetAPI.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Protocol management for Telnet
    /// </summary>
    public static class Telnet
    {
        /// <summary>
        /// Initialize the protocol configuration
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Init()
        {
            if (File.Exists("/etc/inetd.conf"))
            {
                // Although inetd may be used for other purposes, we don't explicitly check if the telnet option is enabled or not
                bool serviceEnabled = await Command.ExecQuery("systemctl", "is-enabled -q inetd.service");
                await Manager.SetProtocol(NetworkProtocol.Telnet, serviceEnabled);
            }
        }

        /// <summary>
        /// Configure the Telnet server
        /// </summary>
        /// <param name="enabled">Enable Telnet</param>
        /// <param name="port">Port</param>
        /// <returns>Configuration result</returns>
        public static async Task<Message> Configure(bool? enabled, int port)
        {
            if (port > 0)
            {
                return new Message(MessageType.Error, "Changing the Telnet port requires manual configuration of /etc/inetd.conf");
            }

            // Enable Telnet
            if (enabled == true && !Manager.IsEnabled(NetworkProtocol.Telnet))
            {
                string startOutput = await Command.Execute("systemctl", "start inetd.service");
                string enableOutput = await Command.Execute("systemctl", "enable -q inetd.service");
                await Manager.SetProtocol(NetworkProtocol.Telnet, true);
                return new Message(MessageType.Success, string.Join('\n', startOutput, enableOutput).Trim());
            }

            // Disable Telnet
            if (enabled == false && Manager.IsEnabled(NetworkProtocol.Telnet))
            {
                string stopOutput = await Command.Execute("systemctl", "stop inetd.service");
                string disableOutput = await Command.Execute("systemctl", "disable -q inetd.service");
                await Manager.SetProtocol(NetworkProtocol.Telnet, false);
                return new Message(MessageType.Success, string.Join('\n', stopOutput, disableOutput).Trim());
            }

            // Don't do anything
            return new Message();
        }

        /// <summary>
        /// Report the current state of the Telnet protocols
        /// </summary>
        /// <param name="builder">String builder</param>
        public static void Report(StringBuilder builder)
        {
            if (Manager.IsEnabled(NetworkProtocol.Telnet))
            {
                builder.AppendLine("Telnet is enabled on port 23");
            }
            else
            {
                builder.AppendLine("Telnet is disabled");
            }
        }
    }
}
