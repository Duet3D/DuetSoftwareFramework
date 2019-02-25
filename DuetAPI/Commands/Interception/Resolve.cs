namespace DuetAPI.Commands
{
    /// <summary>
    /// Resolve the code to intercept and return the given message details for its completion.
    /// This command is only permitted in Interception mode!
    /// </summary>
    /// <seealso cref="DuetAPI.Connection.ConnectionType.Intercept"/>
    public class Resolve : Command
    {
        public MessageType Type { get; set; } = MessageType.Success;
        public string Content { get; set; } = "";
    }
}