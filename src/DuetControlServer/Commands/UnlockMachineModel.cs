using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UnlockMachineModel"/> command
    /// </summary>
    public class UnlockMachineModel : DuetAPI.Commands.UnlockMachineModel
    {
        /// <summary>
        /// Source connection of this command. Needed to register the owner of the lock
        /// </summary>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute()
        {
            IPC.LockManager.UnlockMachineModel(SourceConnection);
            return Task.CompletedTask;
        }
    }
}
