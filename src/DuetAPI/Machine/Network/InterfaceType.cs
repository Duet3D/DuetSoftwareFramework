using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported types of network interfaces
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum InterfaceType
    {
        /// <summary>
        /// Wireless network interface
        /// </summary>
        [EnumMember(Value = "wifi")]
        WiFi,

        /// <summary>
        /// Wired network interface
        /// </summary>
        [EnumMember(Value = "lan")]
        LAN
    }

}
