namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// Enter connection mode for the plugin service.
    /// This init message is used for internal IPC and should not be used by third-party plugins!
    /// </summary>
    public sealed class PluginServiceInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public PluginServiceInitMessage() => Mode = ConnectionMode.PluginService;
    }
}