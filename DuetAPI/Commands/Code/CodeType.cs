using Newtonsoft.Json;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Type of a generic G/M/T-code. If none is applicable, it is treated as a comment
    /// </summary>
    [JsonConverter(typeof(CharEnumConverter))]
    public enum CodeType
    {
        /// <summary>
        /// Comment type (ignored during execution)
        /// </summary>
        Comment = 'C',

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
