using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Supported network protocols
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
    public enum NetworkProtocol
    {
        /// <summary>
        /// HTTP protocol
        /// </summary>
        HTTP,

        /// <summary>
        /// FTP protocol
        /// </summary>
        FTP,

        /// <summary>
        /// Telnet protocol
        /// </summary>
        Telnet
    }
}
