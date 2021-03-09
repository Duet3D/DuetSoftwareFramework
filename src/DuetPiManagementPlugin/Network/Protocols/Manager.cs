using DuetAPI.ObjectModel;
using DuetAPIClient;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Manage network interfaces
    /// </summary>
    public static class Manager
    {
        /// <summary>
        /// Set of currently enabled network protocols
        /// </summary>
        private static readonly HashSet<NetworkProtocol> _enabledProtocols = new();

        /// <summary>
        /// Check if the given protocol is enabled
        /// </summary>
        /// <param name="protocol">Network protocol</param>
        /// <returns>True if the protocol is enabled</returns>
        public static bool IsEnabled(NetworkProtocol protocol) => _enabledProtocols.Contains(protocol);

        /// <summary>
        /// Update the status of a network protocol
        /// </summary>
        /// <param name="protocol">Protocol to update</param>
        /// <param name="enabled">If it is enabled or not</param>
        /// <returns></returns>
        public static async Task SetProtocol(NetworkProtocol protocol, bool enabled)
        {
            // Update the object model
            using CommandConnection commandConnection = new();
            await commandConnection.Connect("/var/run/dsf/dcs.sock", Program.CancellationToken);
            await commandConnection.SetNetworkProtocol(protocol, enabled);

            // Store this internally as well
            if (enabled)
            {
                _enabledProtocols.Add(protocol);
            }
            else
            {
                _enabledProtocols.Remove(protocol);
            }
        }

        /// <summary>
        /// Initialize the network protocol settings
        /// </summary>
        /// <returns></returns>
        public static async Task Init()
        {
            await FTP.Init();
            await HTTP.Init();
            // SFTP depends on SSH
            await SSH.Init();
            await Telnet.Init();
        }

        /// <summary>
        /// Configure network protocols
        /// </summary>
        /// <param name="protocol">Network protocol number</param>
        /// <param name="enabled">Enable or disable this protocol (or don't change the state)</param>
        /// <param name="secure">Secure or unsecure access</param>
        /// <param name="port">Optional port number</param>
        /// <returns>Configuration message</returns>
        public static async Task<Message> ConfigureProtocols(int protocol, bool? enabled, bool secure, int port)
        {
            Message codeResult;
            switch (protocol)
            {
                // HTTP/HTTPS
                case 0:
                    // HTTP/HTTPS
                    if (enabled != null || port > 0)
                    {
                        codeResult = await HTTP.Configure(enabled, port, secure);
                    }
                    else
                    {
                        StringBuilder builder = new();
                        await HTTP.Report(builder);
                        return new Message(MessageType.Success, builder.ToString().TrimEnd());
                    }
                    break;

                // FTP/SFTP
                case 1:
                    if (secure)
                    {
                        // SFTP
                        if (enabled != null || port > 0)
                        {
                            codeResult = await SFTP.Configure(enabled, port);
                        }
                        else
                        {
                            StringBuilder builder = new();
                            SFTP.Report(builder);
                            return new Message(MessageType.Success, builder.ToString().TrimEnd());
                        }
                    }
                    else
                    {
                        // FTP
                        if (enabled != null || port > 0)
                        {
                            codeResult = await FTP.Configure(enabled, port);
                        }
                        else
                        {
                            StringBuilder builder = new();
                            FTP.Report(builder);
                            return new Message(MessageType.Success, builder.ToString().TrimEnd());
                        }
                    }
                    break;

                // Telnet/SSH
                case 2:
                    if (secure)
                    {
                        // SSH
                        if (enabled != null || port > 0)
                        {
                            codeResult = await SSH.Configure(enabled, port);
                        }
                        else
                        {
                            StringBuilder builder = new();
                            SSH.Report(builder);
                            return new Message(MessageType.Success, builder.ToString().TrimEnd());
                        }
                    }
                    else
                    {
                        // Telnet
                        if (enabled != null || port > 0)
                        {
                            codeResult = await Telnet.Configure(enabled, port);
                        }
                        else
                        {
                            StringBuilder builder = new();
                            Telnet.Report(builder);
                            return new Message(MessageType.Success, builder.ToString().TrimEnd());
                        }
                    }
                    break;

                // Unknown protocol
                default:
                    codeResult = new Message(MessageType.Error, $"Unknown protocol number {protocol}");
                    break;
            }
            return codeResult;
        }

        /// <summary>
        /// Report the status of all protocols
        /// </summary>
        /// <returns>Protocol report</returns>
        public static async Task<Message> ReportProtocols()
        {
            StringBuilder builder = new();
            await HTTP.Report(builder);
            FTP.Report(builder);
            SFTP.Report(builder);
            Telnet.Report(builder);
            SSH.Report(builder);
            return new Message(MessageType.Success, builder.ToString().Trim());
        }
    }
}
