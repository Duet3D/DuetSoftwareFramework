using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Enumeration of supported HTTP responses
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
    public enum HttpResponseType
    {
        /// <summary>
        /// HTTP status code without payload
        /// </summary>
        StatusCode,

        /// <summary>
        /// Plain text (UTF-8)
        /// </summary>
        PlainText,

        /// <summary>
        /// JSON-formatted data
        /// </summary>
        JSON,

        /// <summary>
        /// File content. Response must hold the absolute path to the file to return
        /// </summary>
        File
    }
}
