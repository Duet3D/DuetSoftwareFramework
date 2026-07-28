using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Set the WiFi country code. This is a global setting on Linux, so it is applied to every WiFi
/// interface in the object model
/// </summary>
[RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
public partial class SetWifiCountry : Command
{
    /// <summary>
    /// New WiFi country code, or null to clear it
    /// </summary>
    public string? CountryCode { get; set; }
}
