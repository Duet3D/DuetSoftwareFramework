using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Defines what a user is allowed to do
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<AccessLevel>))]
    public enum AccessLevel
    {
        /// <summary>
        /// Changes to the system and/or operation are not permitted
        /// </summary>
        ReadOnly,

        /// <summary>
        /// Changes to the system and/or operation are permitted
        /// </summary>
        ReadWrite
    }

    /// <summary>
    /// Context for AccessLevel serialization
    /// </summary>
    [JsonSerializable(typeof(AccessLevel))]
    public partial class AccessLevelContext : JsonSerializerContext { }
}
