using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SyncObjectModel"/> command
    /// </summary>
    public sealed class SyncObjectModel : DuetAPI.Commands.SyncObjectModel
    {
        /// <summary>
        /// Waits for the machine model to be fully updated from RepRapFirmware
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => Model.Updater.WaitForFullUpdate(Program.CancellationToken);
    }
}
