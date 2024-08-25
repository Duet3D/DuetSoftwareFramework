using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Image formats for parsed thumbnails
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter<ThumbnailInfoFormat>))]
    public enum ThumbnailInfoFormat
    {
        /// <summary>
        /// Joint Photographic Experts Group
        /// </summary>
        JPEG,

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
