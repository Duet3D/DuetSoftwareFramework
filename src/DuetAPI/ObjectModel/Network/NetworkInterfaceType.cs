using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Supported types of network interfaces
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
    public enum NetworkInterfaceType
    {
        /// <summary>
        /// Wired network interface
        /// </summary>
        LAN,

        /// <summary>
        /// Wireless network interface
        /// </summary>
        WiFi
    }
}
