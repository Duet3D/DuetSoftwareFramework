using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Defines what a user is allowed to do
    /// </summary>
    [JsonConverter(typeof(Utility.JsonCamelCaseStringEnumConverter))]
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
}
