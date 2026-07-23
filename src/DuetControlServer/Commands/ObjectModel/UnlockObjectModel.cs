using DuetControlServer.IPC;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UnlockObjectModel"/> command
    /// </summary>
#pragma warning disable CS0618
    public sealed class UnlockObjectModel : DuetAPI.Commands.UnlockObjectModel, IConnectionCommand
#pragma warning restore CS0618
    {
        /// <summary>
        /// Source connection of this command. Needed to register the owner of the lock
        /// </summary>
        public Connection? Connection { get; set; }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => LockManager.UnlockMachineModel(Connection!);
    }
}
