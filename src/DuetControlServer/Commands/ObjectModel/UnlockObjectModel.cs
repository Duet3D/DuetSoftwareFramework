using DuetControlServer.IPC;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UnlockObjectModel"/> command
    /// </summary>
    public sealed class UnlockObjectModel : DuetAPI.Commands.UnlockObjectModel, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command. Needed to register the owner of the lock
        /// </summary>
        public Connection? Connection { get; set; }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (!Settings.NoSpi)
            {
                throw new InvalidOperationException("Command is only supported in non-SPI mode");
            }

            await LockManager.UnlockMachineModel(Connection!);
        }
    }
}
