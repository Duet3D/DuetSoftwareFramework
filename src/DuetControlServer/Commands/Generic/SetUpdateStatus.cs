using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SetUpdateStatus"/> command
    /// </summary>
    public sealed class SetUpdateStatus : DuetAPI.Commands.SetUpdateStatus
    {
        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.IsUpdating = Updating;
            }
        }
    }
}
