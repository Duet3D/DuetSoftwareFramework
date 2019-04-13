namespace DuetAPI.Connection.InitMessages
{
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