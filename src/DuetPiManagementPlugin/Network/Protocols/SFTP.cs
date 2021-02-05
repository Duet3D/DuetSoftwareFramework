using DuetAPI.ObjectModel;
using System.Text;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Protocol management for SFTP
    /// </summary>
    public static class SFTP
    {
        /// <summarySFTP>
        /// Configure the FTP server
        /// </summary>
        /// <param name="enabled">Enable Telnet</param>
        /// <param name="port">Port</param>
        /// <param name="secure"></param>
        /// <returns>Configuration result</returns>
        public static Task<Message> Configure(bool? enabled, int? port) => SSH.Configure(null, enabled, port);

        /// <summary>
        /// Report the current state of the SFTP protocol
        /// </summary>
        /// <param name="builder">String builder</param>
        public static void Report(StringBuilder builder)
        {
            if (Manager.EnabledProtocols.Contains(NetworkProtocol.SFTP))
            {
                builder.AppendLine($"SFTP is enabled on port {SSH.Port}");
            }
            else
            {
                builder.AppendLine("SFTP is disabled");
            }
        }
    }
}
