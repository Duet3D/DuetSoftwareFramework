using System.Text.Json.Serialization;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Type of the intercepting connection
    /// </summary>
    /// <seealso cref="InitMessages.InterceptInitMessage"/>
    [JsonConverter(typeof(JsonStringEnumConverter<InterceptionMode>))]
    public enum InterceptionMode
    {
        /// <summary>
        /// Intercept codes before they are internally processed by the control server
        /// </summary>
        Pre,

        /// <summary>
        /// Intercept codes after the initial processing of the control server but before they are forwarded to the RepRapFirmware controller
        /// </summary>
        Post,

        /// <summary>
        /// Receive a notification for executed codes. In this state the final result can be still changed
        /// </summary>
        Executed
    }
}
