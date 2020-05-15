using DuetAPI.Connection;
using DuetAPI.Machine;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Resolve the code to intercept and return the given message details for its completion.
    /// </summary>
    /// <remarks>
    /// This command is only permitted in <see cref="ConnectionMode.Intercept"/> mode
    /// </remarks>
    public class Resolve : Command
    {
        /// <summary>
        /// Type of the resolving message
        /// </summary>
        public MessageType Type { get; set; } = MessageType.Success;

        /// <summary>
        /// Content of the resolving message
        /// </summary>
        public string Content { get; set; }
    }
}