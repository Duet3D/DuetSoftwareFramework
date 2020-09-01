using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Type of a generic G/M/T-code. If none is applicable, it is treated as a comment
    /// </summary>
    [JsonConverter(typeof(JsonCharEnumConverter))]
    public enum CodeType
    {
        /// <summary>
        /// Whole line comment
        /// </summary>
        Comment = 'Q',

        /// <summary>
        /// G-code
        /// </summary>
        GCode = 'G',

        /// <summary>
        /// M-code
        /// </summary>
        MCode = 'M',

        /// <summary>
        /// T-code
        /// </summary>
        TCode = 'T'
    }
}
