using DuetControlServer.IPC;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.LockObjectModel"/> command
    /// </summary>
    public class LockMachineModel : DuetAPI.Commands.LockObjectModel, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection Connection { get; set; }

        /// <summary>
        /// Lock the machine model for write access
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => IPC.LockManager.LockMachineModel(Connection);
    }
}
