using DuetControlServer.IPC;
using DuetControlServer.SPI;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Flush"/> command
    /// </summary>
    public sealed class Flush : DuetAPI.Commands.Flush, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection Connection { get; set; }

        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task<bool> Execute()
        {
            Code codeBeingIntercepted = IPC.Processors.CodeInterception.GetCodeBeingIntercepted(Connection);
            return (codeBeingIntercepted != null) ? Interface.Flush(codeBeingIntercepted) : Interface.Flush(Channel);
        }
    }
}
