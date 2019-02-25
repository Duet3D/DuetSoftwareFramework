using DuetAPI.Commands;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace DuetControlServer.RepRapFirmware
{
    public static class Connector
    {
        private static readonly BufferBlock<BaseCommand> pendingCommands = new BufferBlock<BaseCommand>();
        // TODO: Implement flush mechanism

        public static void Connect()
        {
            // TODO handshake and version check
        }

        public static async Task Run()
        {
            // TODO:
            // 1) Create connection
            // 2) Run config.g/config.g.bak
            // 3) Deal with internal code streams
            // 4) Deal with pausing
            // 5) Keep the model up-to-date
            await Task.Delay(-1, Program.CancelSource.Token);
        }

        public static Task<CodeResult> ProcessCode(Code code)
        {
            // TODO: Return TaskCompletionSource, enqueue code+TCS and deal with the actual execution in Run()
            return Task.FromResult(new CodeResult());
        }
    }
}