using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of possible input shaping methods
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
    public enum InputShapingType
    {
        /// <summary>
        /// None
        /// </summary>
        None,

        /// <summary>
        /// ZVD
        /// </summary>
        ZVD,

        /// <summary>
        /// ZVDD
        /// </summary>
        ZVDD,

        /// <summary>
        /// EI2 (2-hump)
        /// </summary>
        EI2,

        /// <summary>
        /// EI3 (3-hump)
        /// </summary>
        EI3
    }
}
