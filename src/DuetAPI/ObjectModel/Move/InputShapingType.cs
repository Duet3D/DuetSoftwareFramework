using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of possible input shaping methods
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<InputShapingType>))]
    public enum InputShapingType
    {
        /// <summary>
        /// None
        /// </summary>
        None,

        /// <summary>
        /// MZV
        /// </summary>
        MZV,

        /// <summary>
        /// ZVD
        /// </summary>
        ZVD,

        /// <summary>
        /// ZVDD
        /// </summary>
        ZVDD,

        /// <summary>
        /// ZVDDD
        /// </summary>
        ZVDDD,

        /// <summary>
        /// EI2 (2-hump)
        /// </summary>
        EI2,

        /// <summary>
        /// EI3 (3-hump)
        /// </summary>
        EI3,

        /// <summary>
        /// Custom
        /// </summary>
        Custom
    }

    /// <summary>
    /// Context for InputShapingType serialization
    /// </summary>
    [JsonSerializable(typeof(InputShapingType))]
    public partial class InputShapingTypeContext : JsonSerializerContext { }
}
