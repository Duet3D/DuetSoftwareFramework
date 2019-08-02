namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// Enter command-based connection mode
    /// In this conneciton mode nearly all of the commands in the <see cref="Commands"/> namespace can be used
    /// </summary>
    public class CommandInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public CommandInitMessage()
        {
            Mode = ConnectionMode.Command;
        }
    }
}