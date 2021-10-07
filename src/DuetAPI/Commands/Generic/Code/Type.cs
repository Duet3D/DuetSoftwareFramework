using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Type of a generic G/M/T-code
    /// </summary>
    [JsonConverter(typeof(JsonCharEnumConverter))]
    public enum CodeType
    {
        /// <summary>
        /// Undetermined
        /// </summary>
        None = '\0',

        /// <summary>
        /// Whole line comment
        /// </summary>
        Comment = 'Q',

        /// <summary>
        /// Meta G-code keyword (not sent as a code to RRF)
        /// </summary>
        /// <remarks>
        /// Codes of this type are not sent to RRF in binary representation
        /// </remarks>
        Keyword = 'K',

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
