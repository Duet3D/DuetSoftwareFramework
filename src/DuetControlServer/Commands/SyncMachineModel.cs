using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SyncMachineModel"/> command
    /// </summary>
    public class SyncMachineModel : DuetAPI.Commands.SyncMachineModel
    {
        /// <summary>
        /// Waits for the machine model to be fully updated from RepRapFirmware
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => Model.Updater.WaitForFullUpdate();
    }
}
