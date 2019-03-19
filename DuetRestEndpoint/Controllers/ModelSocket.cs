using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetRestEndpoint.Controllers
{
    /// <summary>
    /// This class takes care of WebSocket-based communication.
    /// At the moment it is only used to provide continuous machine model updates.
    /// </summary>
    public static class ModelSocket
    {
        /// <summary>
        /// Deal with a newly opened WebSocket
        /// </summary>
        /// <param name="context">HTTP context</param>
        /// <param name="webSocket">WebSocket connection</param>
        /// <returns></returns>
        public static async Task Process(HttpContext context, WebSocket webSocket)
        {
            // 1. Authentication
            // TODO

            // 2. Grab full model copy and send it over initially
            JObject modelData = Services.ModelProvider.GetFull();
            byte[] txData = Encoding.UTF8.GetBytes(modelData.ToString());
            await webSocket.SendAsync(txData, WebSocketMessageType.Text, true, default(CancellationToken));

            // 3. Keep sending updates to the client and wait for "OK" after each update
            do
            {
                // 3a. Start waiting for updates. Sending data to the client might take a while
                Task waitForUpdate = Services.ModelProvider.WaitForUpdate();

                // 3b. Wait for request from the client
                byte[] receivedBytes = new byte[8];
                await webSocket.ReceiveAsync(receivedBytes, default(CancellationToken));
                string receivedData = Encoding.UTF8.GetString(receivedBytes).Trim();

                // 3c. Check if the client has acknowledged the received data
                if (!receivedData.Equals("OK", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Terminate the connection if anything else than "OK" is received
                    await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Invalid command", default(CancellationToken));
                    break;
                }

                // 3d. Wait for another update to occur
                await waitForUpdate;

                // 3e. Fetch latest data and send the patch to the client
                JObject diff = Services.ModelProvider.GetPatch(ref modelData);
                txData = Encoding.UTF8.GetBytes(diff.ToString());
                await webSocket.SendAsync(txData, WebSocketMessageType.Text, true, default(CancellationToken));
            }
            while (webSocket.State == WebSocketState.Open);
        }
    }
}
