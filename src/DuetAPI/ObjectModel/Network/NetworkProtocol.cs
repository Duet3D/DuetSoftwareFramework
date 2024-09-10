using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Supported network protocols
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<NetworkProtocol>))]
    public enum NetworkProtocol
    {
        /// <summary>
        /// HTTP protocol
        /// </summary>
        HTTP,

        /// <summary>
        /// HTTPS protocol
        /// </summary>
        HTTPS,

        /// <summary>
        /// FTP protocol
        /// </summary>
        FTP,

        /// <summary>
        /// SFTP protocol
        /// </summary>
        SFTP,

        /// <summary>
        /// Telnet protocol
        /// </summary>
        Telnet,

        /// <summary>
        /// SSH protocol
        /// </summary>
        SSH
    }
}
