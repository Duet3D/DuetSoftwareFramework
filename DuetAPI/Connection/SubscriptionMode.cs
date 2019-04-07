using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Type of the model subscription
    /// </summary>
    /// <seealso cref="InitMessages.SubscribeInitMessage"/>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SubscriptionMode
    {
        /// <summary>
        /// Receive full object model after every update
        /// </summary>
        Full,
        
        /// <summary>
        /// Receive only updated JSON fragments of the object model
        /// </summary>
        Patch
    }
}
