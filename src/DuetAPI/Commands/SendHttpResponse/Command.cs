namespace DuetAPI.Commands
{
    /// <summary>
    /// Send a response to a received HTTP request
    /// </summary>
    public class SendHttpResponse
    {
        /// <summary>
        /// HTTP or WebSocket status code to return. If this is greater than or equal to 1000, the WebSocket is closed
        /// </summary>
        /// <remarks>Codes greater than 1000 represent WebSocket status codes (1000 = normal close)</remarks>
        public int StatusCode { get; set; }

        /// <summary>
        /// Content to return. If this is null or empty and a WebSocket is connected, the connection is closed
        /// </summary>
        public string Response { get; set; }

        /// <summary>
        /// Type of the content to return. Ignored if a WebSocket is connected
        /// </summary>
        public HttpResponseType ResponseType { get; set; }
    }
}
