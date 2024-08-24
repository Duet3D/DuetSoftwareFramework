using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
	/// <summary>
	/// State of a channel
	/// </summary>
	[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<InputChannelState>))]
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

    /// <summary>
    /// Context for InputChannelState serialization
    /// </summary>
    [JsonSerializable(typeof(InputChannelState))]
    public partial class InputChannelStateContext : JsonSerializerContext { }
}
