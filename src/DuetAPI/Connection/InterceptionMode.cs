using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Type of the intercepting connection
    /// </summary>
    /// <seealso cref="InitMessages.InterceptInitMessage"/>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum InterceptionMode
    {
        /// <summary>
        /// Intercept codes before they are internally processed by the control server
        /// </summary>
        Pre,

        /// <summary>
        /// Intercept codes after the initial processing of the control server but before they are forwarded to the RepRapFirmware controller
        /// </summary>
        Post
    }
}
