using DuetAPI;
using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes
{
    static class Execution
    {
        private static AsyncCollection<Code>[] _queuedCodes;

        /// <summary>
        /// Start dealing with codes in an ordered way
        /// </summary>
        public static Task ProcessCodes()
        {
            CodeChannel[] channels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

            _queuedCodes = new AsyncCollection<Code>[channels.Length];
            Task[] tasks = new Task[channels.Length];

            foreach (CodeChannel channel in channels)
            {
                _queuedCodes[(int)channel] = new AsyncCollection<Code>();
                tasks[(int)channel] = ExecuteCodes(channel);
            }

            return Task.WhenAll(tasks);
        }

        /// <summary>
        /// Enqueue a G/M/T-code for execution
        /// </summary>
        /// <param name="code">Code to execute</param>
        public static void Execute(Code code) => _queuedCodes[(int)code.Channel].Add(code);

        /// <summary>
        /// Execute G/M/T-codes in the right order.
        /// </summary>
        /// <param name="channel">Code channel to deal with</param>
        /// <returns>Asynchronous task</returns
        /// <remarks>Parallel execution of G/M/T-codes creates bad side effects so don't allow this any more</remarks>
        private static async Task ExecuteCodes(CodeChannel channel)
        {
            AsyncCollection<Code> queuedCodes = _queuedCodes[(int)channel];
            while (await queuedCodes.OutputAvailableAsync(Program.CancelSource.Token))
            {
                Code code = await queuedCodes.TakeAsync(Program.CancelSource.Token);
                try
                {
                    CodeResult result = await code.Process();
                    code.SetResult(result);
                }
                catch (AggregateException ae)
                {
                    code.SetException(ae.InnerException);
                }
            }
        }
    }
}
