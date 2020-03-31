using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Distance unit used for positioning
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter<DistanceUnit>))]
    public enum DistanceUnit
    {
        /// <summary>
        /// Millimeters
        /// </summary>
        MM,

        /// <summary>
        /// Inches
        /// </summary>
        Inch
    }
}
