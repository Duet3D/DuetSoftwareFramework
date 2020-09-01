using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// States of a tool
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
    public enum ToolState
    {
        /// <summary>
        /// Tool is turned off
        /// </summary>
        Off,

        /// <summary>
        /// Tool is active
        /// </summary>
        Active,

        /// <summary>
        /// Tool is in standby
        /// </summary>
        Standby
    }
}
