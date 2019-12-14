using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.LockMachineModel"/> command
    /// </summary>
    public class LockMachineModel : DuetAPI.Commands.LockMachineModel
    {
        /// <summary>
        /// Source connection of this command. Needed to register the owner of the lock
        /// </summary>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Lock the machine model for write access
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => IPC.LockManager.LockMachineModel(SourceConnection);
    }
}
