using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
using System.Net;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// HTTP endpoint for closing the locally running DWC browser window
    /// </summary>
    public static class Browser
    {
        /// <summary>
        /// Namespace of the HTTP endpoint
        /// </summary>
        private const string EndpointNamespace = "DuetPiManagementPlugin";

        /// <summary>
        /// Path of the HTTP endpoint (exposed as /machine/DuetPiManagementPlugin/closeBrowser)
        /// </summary>
        private const string EndpointPath = "closeBrowser";

        /// <summary>
        /// Flag the DuetPi launcher passes to the kiosk Chromium. Matching on it lets us close only the
        /// auto-started DWC window and leave any manually opened browser instances untouched
        /// </summary>
        private const string KioskMarker = "--app-auto-launched";

        /// <summary>
        /// Register the close-browser endpoint and keep serving it until the plugin is stopped
        /// </summary>
        /// <param name="socketPath">UNIX socket path for IPC</param>
        public static async Task RunAsync(string socketPath)
        {
            try
            {
                using CommandConnection connection = new();
                await connection.ConnectAsync(socketPath, Program.CancellationToken);

                using HttpEndpointUnixSocket socket = await connection.AddHttpEndpointAsync(HttpEndpointType.POST, EndpointNamespace, EndpointPath, cancellationToken: Program.CancellationToken);
                socket.OnEndpointRequestReceived += OnRequestReceived;

                try
                {
                    await Task.Delay(-1, Program.CancellationToken);
                }
                finally
                {
                    if (connection.IsConnected)
                    {
                        await connection.RemoveHttpEndpointAsync(HttpEndpointType.POST, EndpointNamespace, EndpointPath);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException)
            {
                // Plugin is being stopped
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to serve {EndpointPath} endpoint: {e.Message}");
            }
        }

        /// <summary>
        /// Handle an incoming close request
        /// </summary>
        private static async void OnRequestReceived(HttpEndpointUnixSocket unixSocket, HttpEndpointConnection requestConnection)
        {
            // The event delegate owns the connection and must dispose it again
            using (requestConnection)
            {
                try
                {
                    ReceivedHttpRequest request = await requestConnection.ReadRequestAsync(Program.CancellationToken);

                    // Closing the kiosk must only be possible from the machine itself, never from a remote
                    // DWC session. The remote IP is set by DuetWebServer from the real transport peer
                    if (!IsFromLoopback(request))
                    {
                        await requestConnection.SendResponseAsync(403, "Only available from localhost", HttpResponseType.StatusCode, Program.CancellationToken);
                        return;
                    }

                    await requestConnection.SendResponseAsync(204, string.Empty, HttpResponseType.StatusCode, Program.CancellationToken);
                    await Command.ExecuteAsync("pkill", $"-f -- {KioskMarker}");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.WriteLine($"Failed to handle {EndpointPath} request: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Check whether a request originated from the local machine
        /// </summary>
        private static bool IsFromLoopback(ReceivedHttpRequest request)
        {
            if (IPAddress.TryParse(request.RemoteIPAddress, out IPAddress? address))
            {
                if (address.IsIPv4MappedToIPv6)
                {
                    address = address.MapToIPv4();
                }
                return IPAddress.IsLoopback(address);
            }
            return false;
        }
    }
}
