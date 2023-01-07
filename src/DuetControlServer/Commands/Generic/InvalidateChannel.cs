using DuetControlServer.IPC;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InvalidateChannel"/> command
    /// </summary>
    public sealed class InvalidateChannel : DuetAPI.Commands.InvalidateChannel, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection? Connection { get; set; }

        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            // Check if the corresponding code channel has been disabled
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Inputs[Channel] is null)
                {
                    throw new InvalidOperationException("Requested code channel has been disabled");
                }
            }

            // Wait for all codes and files to be invalidated
            await SPI.Interface.AbortAllAsync(Channel);
        }
    }
}
