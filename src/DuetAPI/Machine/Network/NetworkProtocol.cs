using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported network protocols
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum NetworkProtocol
    {
        /// <summary>
        /// HTTP protocol
        /// </summary>
        [EnumMember(Value = "http")]
        HTTP,

        /// <summary>
        /// FTP protocol
        /// </summary>
        [EnumMember(Value = "ftp")]
        FTP,

        /// <summary>
        /// Telnet protocol
        /// </summary>
        [EnumMember(Value = "telnet")]
        Telnet
    }
}
