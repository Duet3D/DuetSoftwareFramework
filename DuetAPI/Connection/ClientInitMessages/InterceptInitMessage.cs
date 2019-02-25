using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Type of the intercepting connection
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum InterceptionMode
    {
        /// <summary>
        /// Intercept codes before they are internally processed by the control server
        /// </summary>
        Pre,
        
        /// <summary>
        /// Intercept codes after the initial processing of the control server but before they are forwarded
        /// to the RepRapFirmware controller
        /// </summary>
        Post
    }

    /// <summary>
    /// Enter interception mode
    /// Whenever a code is received, the connection must respond with one of
    /// - <cref see="DuetAPI.Commands.Ignore">Ignore</cref> to pass through the code without modifications
    /// - <cref see="DuetAPI.Commands.Resolve">Resolve</cref> to resolve the current code and to return a message
    /// In addition the interceptor may issue custom commands once a code has been received
    /// Do not attempt to perform commands before an intercepting code is received, else the order of
    /// command execution cannot be guaranteed
    /// </summary>
    public class InterceptInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Intercept codes either before they are internally processed (pre)
        /// or intercept them before they are forwarded to RepRapFirmware (post)
        /// </summary>
        public InterceptionMode InterceptionMode { get; set; }
    }
}