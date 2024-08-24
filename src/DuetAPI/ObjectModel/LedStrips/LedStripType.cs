using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Types of supported LED strips
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LedStripType>))]
    public enum LedStripType
    {
        /// <summary>
        /// DotStar LED strip
        /// </summary>
        DotStar,

        /// <summary>
        /// NeoPixel LED strip with only RGB capability
        /// </summary>
        NeoPixel_RGB,

        /// <summary>
        /// NeoPixel RGB LED strip with additional white output
        /// </summary>
        NeoPixel_RGBW
    }

    /// <summary>
    /// Context for LedStripType serialization
    /// </summary>
    [JsonSerializable(typeof(LedStripType))]
    public partial class LedStripTypeContext : JsonSerializerContext { }
}
