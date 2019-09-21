using DuetAPI;
using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Codes
{
    static class Execution
    {
        private static AsyncCollection<Code>[] _queuedCodes;
        private static bool[] _invalidating;

        /// <summary>
        /// Start dealing with codes in an ordered way
        /// </summary>
        /// <remarks>
        /// This class is currently NOT used by file prints since it would conflict with the buffering concept
        /// </remarks>
        public static Task ProcessCodes()
        {
            int numCodeChannels = Enum.GetValues(typeof(CodeChannel)).Length;

            _queuedCodes = new AsyncCollection<Code>[numCodeChannels];
            _invalidating = new bool[numCodeChannels];
            Task[] tasks = new Task[numCodeChannels];

            for (int i = 0; i < numCodeChannels; i++)
            {
                _queuedCodes[i] = new AsyncCollection<Code>();
                _invalidating[i] = false;
                tasks[i] = ExecuteCodes((CodeChannel)i);
            }

            return Task.WhenAll(tasks);
        }

        /// <summary>
        /// Invalidate all buffered codes of the given channel
        /// </summary>
        /// <param name="channel">Code channel to invalidate</param>
        public static void Invalidate(CodeChannel channel) => _invalidating[(int)channel] = true;

        /// <summary>
        /// Enqueue a G/M/T-code for execution
        /// </summary>
        /// <param name="code">Code to execute</param>
        public static void Execute(Code code) => _queuedCodes[(int)code.Channel].Add(code);

        /// <summary>
        /// Execute incoming G/M/T-codes in sequential order
        /// </summary>
        /// <param name="channel">Code channel to deal with</param>
        /// <returns>Asynchronous task</returns
        /// <remarks>Parallel execution of G/M/T-codes creates bad side effects so don't allow this any more</remarks>
        private static async Task ExecuteCodes(CodeChannel channel)
        {
            AsyncCollection<Code> queuedCodes = _queuedCodes[(int)channel];
            while (await queuedCodes.OutputAvailableAsync(Program.CancelSource.Token))
            {
                _invalidating[(int)channel] = false;
                Code code = await queuedCodes.TakeAsync(Program.CancelSource.Token);
                try
                {
                    CodeResult result = await code.Process();
                    code.SetResult(result);
                }
                catch (TaskCanceledException)
                {
                    code.SetCanceled();
                    if (_invalidating[(int)channel])
                    {
                        // Unfortunately AsyncCollection does not provide a simple way to check if there are any more items so use a time-based TCS here
                        using (CancellationTokenSource tcs = new CancellationTokenSource(25))
                        {
                            try
                            {
                                while (await queuedCodes.OutputAvailableAsync(tcs.Token))
                                {
                                    code = await queuedCodes.TakeAsync(tcs.Token);
                                    code.SetCanceled();
                                }
                            }
                            catch
                            {
                                // No more items in this collection
                            }
                        }
                    }
                }
                catch (AggregateException ae)
                {
                    code.SetException(ae.InnerException);
                }
            }
        }
    }
}
