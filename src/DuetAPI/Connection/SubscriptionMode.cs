using System.Text.Json.Serialization;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Type of the model subscription
    /// </summary>
    /// <seealso cref="InitMessages.SubscribeInitMessage"/>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SubscriptionMode
    {
        /// <summary>
        /// Receive full object model after every update
        /// </summary>
        /// <remarks>
        /// Generic messages may or may not be included in the full object model. To keep track of messages reliably,
        /// it is strongly advised to create a subscription in <see cref="Patch"/> mode.
        /// </remarks>
        Full,
        
        /// <summary>
        /// Receive only updated JSON fragments of the object model
        /// </summary>
        Patch
    }
}
