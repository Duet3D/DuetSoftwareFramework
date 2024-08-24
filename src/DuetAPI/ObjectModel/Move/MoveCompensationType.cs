using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Supported compensation types
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<MoveCompensationType>))]
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

    /// <summary>
    /// Context for MoveCompensationType serialization
    /// </summary>
    [JsonSerializable(typeof(MoveCompensationType))]
    public partial class MoveCompensationTypeContext : JsonSerializerContext { }
}
