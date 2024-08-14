using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing the configured log level
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<LogLevel>))]
    public enum LogLevel : byte
    {
        /// <summary>
        /// Log everything including debug messages
        /// </summary>
        Debug,

        /// <summary>
        /// Log information and warning messages
        /// </summary>
        Info,

        /// <summary>
        /// Log warning messages only
        /// </summary>
        Warn,
        
        /// <summary>
        /// Logging is disabled
        /// </summary>
        Off
    }
}
