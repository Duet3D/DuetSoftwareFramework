using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Type of a configured endstop
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<EndstopType>))]
    public enum EndstopType
    {
        /// <summary>
        /// Generic input pin
        /// </summary>
        InputPin,

        /// <summary>
        /// Z-probe acts as an endstop
        /// </summary>
        ZProbeAsEndstop,

        /// <summary>
        /// Motor stall detection stops all the drives when triggered
        /// </summary>
        MotorStallAny,

        /// <summary>
        /// Motor stall detection stops individual drives when triggered
        /// </summary>
        MotorStallIndividual,

        /// <summary>
        /// Unknown type
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Context for EndstopType serialization
    /// </summary>
    [JsonSerializable(typeof(EndstopType))]
    public partial class EndstopTypeContext : JsonSerializerContext { }
}
