using DuetControlServer.IPC;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UnlockObjectModel"/> command
    /// </summary>
    public class UnlockMachineModel : DuetAPI.Commands.UnlockObjectModel, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command. Needed to register the owner of the lock
        /// </summary>
        public Connection Connection { get; set; }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute()
        {
            IPC.LockManager.UnlockMachineModel(Connection);
            return Task.CompletedTask;
        }
    }
}
