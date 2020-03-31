using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported compensation types
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter<MoveCompensationType>))]
	public enum MoveCompensationType
	{
		/// <summary>
		/// No compensation
		/// </summary>
		None,

		/// <summary>
		/// Mesh compensation
		/// </summary>
		Mesh
	}
}
