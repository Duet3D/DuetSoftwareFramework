using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UnlockMachineModel"/> command
    /// </summary>
    public class UnlockMachineModel : DuetAPI.Commands.UnlockMachineModel
    {
        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute()
        {
            IPC.LockManager.UnlockMachineModel();
            return Task.CompletedTask;
        }
    }
}
