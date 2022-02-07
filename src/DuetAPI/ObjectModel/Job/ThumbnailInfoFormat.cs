using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Image formats for parsed thumbnails
    /// </summary>
    [JsonConverter(typeof(JsonLowerCaseStringEnumConverter))]
    public enum ThumbnailInfoFormat
    {
        /// <summary>
        /// Portable Network Graphics
        /// </summary>
        PNG,

        /// <summary>
        /// Quite OK Image
        /// </summary>
        QOI
    }
}
