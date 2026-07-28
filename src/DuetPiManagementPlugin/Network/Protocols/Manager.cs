using DuetAPI.ObjectModel;
using DuetSharedLibrary;
using System;
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
        private static readonly HashSet<NetworkProtocol> _enabledProtocols = [];

        /// <summary>
        /// Lock guarding <see cref="_enabledProtocols"/>, which is written from concurrent protocol initializations
        /// </summary>
        private static readonly object _enabledProtocolsLock = new();

        /// <summary>
        /// Check if the given protocol is enabled
        /// </summary>
        /// <param name="protocol">Network protocol</param>
        /// <returns>True if the protocol is enabled</returns>
        public static bool IsEnabled(NetworkProtocol protocol)
        {
            lock (_enabledProtocolsLock)
            {
                return _enabledProtocols.Contains(protocol);
            }
        }

        /// <summary>
        /// Update the status of a network protocol
        /// </summary>
        /// <param name="protocol">Protocol to update</param>
        /// <param name="enabled">If it is enabled or not</param>
        /// <returns></returns>
        public static async Task SetProtocol(NetworkProtocol protocol, bool enabled)
        {
            // Update the object model
            using InternalCommandConnection commandConnection = new();
            await commandConnection.ConnectAsync(Program.SocketPath, Program.CancellationToken);
            await commandConnection.SetNetworkProtocolAsync(protocol, enabled);

            // Store this internally as well
            lock (_enabledProtocolsLock)
            {
                if (enabled)
                {
                    _enabledProtocols.Add(protocol);
                }
                else
                {
                    _enabledProtocols.Remove(protocol);
                }
            }
        }

        /// <summary>
        /// Initialize the network protocol settings
        /// </summary>
        /// <returns></returns>
        public static async Task Init()
        {
            // Each protocol reads its own configuration, so they are independent of each other.
            // SFTP has no initialization of its own, it is set up along with SSH
            await Task.WhenAll(
                InitProtocol(FTP.Init, "FTP"),
                InitProtocol(HTTP.Init, "HTTP"),
                InitProtocol(SSH.Init, "SSH"),
                InitProtocol(Telnet.Init, "Telnet")
            );
        }

        /// <summary>
        /// Read the settings of a single network protocol
        /// </summary>
        /// <param name="init">Initialization function of the protocol</param>
        /// <param name="protocol">Name of the protocol for diagnostics</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// Each protocol is guarded on its own so that one unavailable service neither stops the plugin
        /// from starting nor keeps the remaining protocols from being read
        /// </remarks>
        private static async Task InitProtocol(Func<Task> init, string protocol)
        {
            try
            {
                await init();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.WriteLine("[err] Failed to read {0} protocol settings: {1}", protocol, e.Message);
            }
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
            StringBuilder builder = new();
            switch (protocol)
            {
                // HTTP/HTTPS
                case 0:
                    // HTTP/HTTPS
                    if (enabled is not null || port > 0)
                    {
                        return await HTTP.Configure(enabled, port, secure);
                    }
                    await HTTP.Report(builder);
                    return new Message(MessageType.Success, builder.ToString().TrimEnd());

                // FTP/SFTP
                case 1:
                    if (secure)
                    {
                        // SFTP
                        if (enabled is not null || port > 0)
                        {
                            return await SFTP.Configure(enabled, port);
                        }
                        SFTP.Report(builder);
                        return new Message(MessageType.Success, builder.ToString().TrimEnd());
                    }
                    else
                    {
                        // FTP
                        if (enabled is not null || port > 0)
                        {
                            return await FTP.Configure(enabled, port);
                        }
                        FTP.Report(builder);
                        return new Message(MessageType.Success, builder.ToString().TrimEnd());
                    }

                // Telnet/SSH
                case 2:
                    if (secure)
                    {
                        // SSH
                        if (enabled is not null || port > 0)
                        {
                            return await SSH.Configure(enabled, port);
                        }
                        SSH.Report(builder);
                        return new Message(MessageType.Success, builder.ToString().TrimEnd());
                    }
                    else
                    {
                        // Telnet
                        if (enabled is not null || port > 0)
                        {
                            return await Telnet.Configure(enabled, port);
                        }
                        Telnet.Report(builder);
                        return new Message(MessageType.Success, builder.ToString().TrimEnd());
                    }

                // Multicast is not supported (3)

                // MQTT is handled by DuetControlServer
                case 4:
                    return new Message();

                // Unknown protocol
                default:
                    return new Message(MessageType.Error, $"Unknown protocol number {protocol}");
            }
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
