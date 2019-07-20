using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported geometry types
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GeometryType
    {
        /// <summary>
        /// Cartesian geometry
        /// </summary>
        [EnumMember(Value = "cartesian")]
        Cartesian,

        /// <summary>
        /// CoreXY geometry
        /// </summary>
        [EnumMember(Value = "coreXY")]
        CoreXY,

        /// <summary>
        /// CoreXY geometry with extra U axis
        /// </summary>
        [EnumMember(Value = "coreXYU")]
        CoreXYU,

        /// <summary>
        /// CoreXY geometry with extra UV axes
        /// </summary>
        [EnumMember(Value = "coreXYUV")]
        CoreXYUV,

        /// <summary>
        /// CoreXZ geometry
        /// </summary>
        [EnumMember(Value = "coreXZ")]
        CoreXZ,

        /// <summary>
        /// Hangprinter geometry
        /// </summary>
        [EnumMember(Value = "Hangprinter")]
        Hangprinter,

        /// <summary>
        /// Delta geometry
        /// </summary>
        [EnumMember(Value = "delta")]
        Delta,

        /// <summary>
        /// Polar geometry
        /// </summary>
        [EnumMember(Value = "Polar")]
        Polar,

        /// <summary>
        /// Rotary delta geometry
        /// </summary>
        [EnumMember(Value = "Rotary delta")]
        RotaryDelta,

        /// <summary>
        /// SCARA geometry
        /// </summary>
        [EnumMember(Value = "Scara")]
        Scara,

        /// <summary>
        /// Unknown geometry
        /// </summary>
        [EnumMember(Value = "unknown")]
        Unknown
    }
}
