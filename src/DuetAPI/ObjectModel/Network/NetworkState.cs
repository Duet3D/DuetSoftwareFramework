using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
	/// <summary>
	/// Enumeration of possible network interface states
	/// </summary>
	[JsonConverter(typeof(JsonStringEnumConverter<NetworkState>))]
    public enum NetworkState
    {
		/// <summary>
		/// Network disabled
		/// </summary>
		Disabled,

		/// <summary>
		/// Network enabled but not started yet
		/// </summary>
		Enabled,

		/// <summary>
		/// Starting up (used by WiFi networking in standalone mode)
		/// </summary>
		Starting1,

        /// <summary>
        /// Starting up (used by WiFi networking in standalone mode)
        /// </summary>
        Starting2,

		/// <summary>
		/// Running and in the process of switching between modes (used by WiFi networking in standalone mode)
		/// </summary>
		ChangingMode,

		/// <summary>
		/// Starting up, waiting for link
		/// </summary>
		EstablishingLink,

		/// <summary>
		/// Link established, waiting for DHCP
		/// </summary>
		[JsonPropertyName("obtainingIP")]
		ObtainingIP,

        /// <summary>
        /// Just established a connection
        /// </summary>
        Connected,

		/// <summary>
		/// Network running
		/// </summary>
		Active,

		/// <summary>
		/// WiFi adapter is idle
		/// </summary>
		Idle
	}
}
