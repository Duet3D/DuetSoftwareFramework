using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
	/// <summary>
	/// State of a channel
	/// </summary>
	[JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
	public enum InputChannelState
	{
		/// <summary>
		/// Channel is idle
		/// </summary>
		Idle,

		/// <summary>
		/// Channel is waiting for more data
		/// </summary>
		Waiting,

		/// <summary>
		/// Channel is ready to perform an action
		/// </summary>
		Ready
	}
}
