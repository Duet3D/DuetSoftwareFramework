using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Type of the model subscription
    /// </summary>
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

    /// <summary>
    /// Enter subscription mode and receive either the full object model or parts of it after every update
    /// </summary>
    public class SubscribeInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public SubscribeInitMessage()
        {
            Mode = ConnectionMode.Subscribe;
        }

        /// <summary>
        /// Type of the subscription
        /// </summary>
        public SubscriptionMode SubscriptionMode { get; set; }
        
        // TODO Add Filtered mode and OM "Path" property here for the new selective mode
        // TODO? Add optional debounce interval
    }
}