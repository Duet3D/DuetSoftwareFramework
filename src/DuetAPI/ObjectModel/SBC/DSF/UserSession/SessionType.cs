using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Types of user sessions
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<SessionType>))]
    public enum SessionType
    {
        /// <summary>
        /// Local client
        /// </summary>
        Local,

        /// <summary>
        /// Remote client via HTTP
        /// </summary>
        HTTP,

        /// <summary>
        /// Remote client via Telnet
        /// </summary>
        Telnet
    }

    /// <summary>
    /// Context for SessionType serialization
    /// </summary>
    [JsonSerializable(typeof(SessionType))]
    public partial class SessionTypeContext : JsonSerializerContext { }
}
