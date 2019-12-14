using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Types of user sessions
    /// </summary>
    [JsonConverter(typeof(Utility.JsonCamelCaseStringEnumConverter))]
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
}
