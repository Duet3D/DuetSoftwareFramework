using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Base file directory for lookups
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FileDirectory>))]
    public enum FileDirectory
    {
        /// <summary>
        /// Filaments directory
        /// </summary>
        Filaments,

        /// <summary>
        /// Firmware directory
        /// </summary>
        Firmware,

        /// <summary>
        /// GCodes directory
        /// </summary>
        GCodes,

        /// <summary>
        /// Macros directory
        /// </summary>
        Macros,

        /// <summary>
        /// Menu directory
        /// </summary>
        Menu,

        /// <summary>
        /// System directory
        /// </summary>
        System,

        /// <summary>
        /// WWW directory
        /// </summary>
        Web
    }
}
