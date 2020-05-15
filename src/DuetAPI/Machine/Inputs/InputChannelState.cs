using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
	/// <summary>
	/// State of a channel
	/// </summary>
	[JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
	public enum InputChannelState
	{
		/// <summary>
		/// Awaiting message acknowledgement
		/// </summary>
		AwaitingAcknowledgement,

		/// <summary>
		/// Channel is idle
		/// </summary>
		Idle,

		/// <summary>
		/// Channel is executing a G/M/T-code
		/// </summary>
		Executing,

		/// <summary>
		/// Channel is waiting for more data
		/// </summary>
		Waiting,

		/// <summary>
		/// Channel is reading a G/M/T-code
		/// </summary>
		Reading
	}
}
