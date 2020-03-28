using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Type of a configured endstop
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
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
}
