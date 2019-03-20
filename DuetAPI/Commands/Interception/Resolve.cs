using DuetAPI.Connection;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Resolve the code to intercept and return the given message details for its completion.
    /// This command is only permitted in Interception mode!
    /// </summary>
    /// <seealso cref="ConnectionMode.Intercept"/>
    public class Resolve : Command
    {
        /// <summary>
        /// Type of the resolving message
        /// </summary>
        public MessageType Type { get; set; } = MessageType.Success;

        /// <summary>
        /// Content of the resolving message
        /// </summary>
        public string Content { get; set; } = "";
    }
}