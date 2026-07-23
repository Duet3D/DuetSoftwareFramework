using DuetControlServer.IPC;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.LockObjectModel"/> command
    /// </summary>
#pragma warning disable CS0618
    public sealed class LockObjectModel : DuetAPI.Commands.LockObjectModel, IConnectionCommand
#pragma warning restore CS0618
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection? Connection { get; set; }

        /// <summary>
        /// Lock the machine model for write access
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => LockManager.LockMachineModel(Connection!);
    }
}
