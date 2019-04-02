using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using DuetAPI.Commands;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// This class accesses RepRapFirmware via SPI and deals with general communication
    /// </summary>
    public static class Connector
    {
        private static readonly BufferBlock<BaseCommand> pendingCommands = new BufferBlock<BaseCommand>();
        private static readonly Dictionary<CodeChannel, BufferBlock<QueuedCode>> queuedCodes = new Dictionary<CodeChannel, BufferBlock<QueuedCode>>();
        // TODO: Implement flush mechanism

        /// <summary>
        /// Initialize physical transfer and perform initial data transfer
        /// </summary>
        public static void Connect()
        {
            // Initialize SPI and GPIO pin
            DataTransfer.Initialize();

            // Do one transfer to ensure both sides are using compatible versions of the data protocol
            DataTransfer.PerformFullTransfer();
        }

        public static async Task Run()
        {
            // TODO:
            // 1) Deal with internal code streams
            // 2) Deal with pausing
            // 3) Keep the model up-to-date
            // 4) When writing codes check maximum encoded size!
            await Task.Delay(-1, Program.CancelSource.Token);
        }

        public static Task<CodeResult> ProcessCode(Code code)
        {
            // TODO: Return TaskCompletionSource, enqueue code+TCS and deal with the actual execution in Run()
            return Task.FromResult(new CodeResult());
        }
    }
}
