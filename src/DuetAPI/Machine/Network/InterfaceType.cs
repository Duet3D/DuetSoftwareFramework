using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported types of network interfaces
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter<InterfaceType>))]
    public enum InterfaceType
    {
        /// <summary>
        /// Wireless network interface
        /// </summary>
        WiFi,

        /// <summary>
        /// Wired network interface
        /// </summary>
        LAN
    }
}
