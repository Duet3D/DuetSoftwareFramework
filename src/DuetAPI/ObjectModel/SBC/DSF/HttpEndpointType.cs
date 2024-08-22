using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of supported HTTP request types
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HttpEndpointType>))]
    public enum HttpEndpointType
    {
        /// <summary>
        /// HTTP GET request
        /// </summary>
        GET,

        /// <summary>
        /// HTTP POST request
        /// </summary>
        POST,

        /// <summary>
        /// HTTP PUT request
        /// </summary>
        PUT,

        /// <summary>
        /// HTTP PATCH request
        /// </summary>
        PATCH,

        /// <summary>
        /// HTTP TRACE request
        /// </summary>
        TRACE,

        /// <summary>
        /// HTTP DELETE request
        /// </summary>
        DELETE,

        /// <summary>
        /// HTTP OPTIONS request
        /// </summary>
        OPTIONS,

        /// <summary>
        /// WebSocket request. This has not been implemented yet but it is reserved for future usage
        /// </summary>
        WebSocket
    }
}
