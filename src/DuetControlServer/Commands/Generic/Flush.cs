using DuetControlServer.IPC;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Flush"/> command
    /// </summary>
    public sealed class Flush : DuetAPI.Commands.Flush, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection Connection { get; set; }

        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task<bool> Execute()
        {
            // Check if the corresponding code channel has been disabled
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Inputs[Channel] == null)
                {
                    throw new InvalidOperationException("Requested code channel has been disabled");
                }
            }

            // Wait for it to be flushed
            Code codeBeingIntercepted = IPC.Processors.CodeInterception.GetCodeBeingIntercepted(Connection, out _);
            return await ((codeBeingIntercepted != null) ? Codes.Processor.FlushAsync(codeBeingIntercepted, false, false, SyncFileStreams) : Codes.Processor.FlushAsync(Channel));
        }
    }
}
