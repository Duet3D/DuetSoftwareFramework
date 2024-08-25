using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Enumeration of possible direct-connect display controllers
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DirectDisplayController>))]
    public enum DirectDisplayController
    {
        /// <summary>
        /// ST7920 controller
        /// </summary>
        ST7920,

        /// <summary>
        /// ST7567 controller
        /// </summary>
        ST7567,

        /// <summary>
        /// ILI9488 controller
        /// </summary>
        ILI9488
    }
}
