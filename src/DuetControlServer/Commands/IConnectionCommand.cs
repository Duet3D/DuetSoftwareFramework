using DuetControlServer.IPC;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Interface for derived command classes to get access to the source connection
    /// </summary>
    public interface IConnectionCommand
    {
        /// <summary>
        /// Source connection of the command
        /// </summary>
        public Connection? Connection { get; set; }
    }
}
