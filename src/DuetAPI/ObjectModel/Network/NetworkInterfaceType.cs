using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Supported types of network interfaces
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkInterfaceType>))]
public enum NetworkInterfaceType
{
    /// <summary>
    /// Wired network interface
    /// </summary>
    [JsonStringEnumMemberName("ethernet")]
    Ethernet,

    /// <summary>
    /// Wireless network interface
    /// </summary>
    [JsonStringEnumMemberName("wifi")]
    WiFi
}
