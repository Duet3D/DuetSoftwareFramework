using DuetAPI.Commands;
using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using DuetAPI.Machine.Heat;

namespace DuetControlServer.SPI
{
    public static class Connector
    {
        private static ushort codeIdCounter = 0;
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
            
            
            // Generate some dummy data...
            Random rnd = new Random(DateTime.Now.Millisecond);
            ModelProvider.Current.Heat.Heaters.Add(new Heater() { Current = 20.0, Name = "Test" });

            do
            {
                ModelProvider.Current.Heat.Heaters[0].Current = 19 + rnd.NextDouble() * 2;
                IPC.Processors.Subscription.Update(ModelProvider.Current);
                await Task.Delay(2000, Program.CancelSource.Token);
            } while (!Program.CancelSource.IsCancellationRequested);
        }

        public static Task<CodeResult> ProcessCode(Code code)
        {
            // TODO: Return TaskCompletionSource, enqueue code+TCS and deal with the actual execution in Run()
            return Task.FromResult(new CodeResult());
        }
    }
}
