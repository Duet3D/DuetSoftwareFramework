using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Main class delegating parallel G/M/T-code execution
    /// </summary>
    public static class Processor
    {
        /// <summary>
        /// Processors per code channel
        /// </summary>
        private static readonly ChannelProcessor[] _processors = new ChannelProcessor[Inputs.Total];

        /// <summary>
        /// Initialize this class
        /// </summary>
        public static void Init()
        {
            for (int input = 0; input < Inputs.Total; input++)
            {
                _processors[input] = new ChannelProcessor((CodeChannel)input);
            }
        }

        /// <summary>
        /// Task representing the lifecycle of this class
        /// </summary>
        /// <returns></returns>
        public static Task Run() => Task.WhenAll(_processors.Select(processor => processor.Run()));

        /// <summary>
        /// Get diagnostics from every channel processor
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        public static void Diagnostics(StringBuilder builder)
        {
            foreach (ChannelProcessor processor in _processors)
            {
                processor.Diagnostics(builder);
            }
        }

        /// <summary>
        /// Get the pipeline state of the firmware stage from a given channel
        /// </summary>
        /// <param name="channel"></param>
        internal static Pipelines.PipelineStackItem GetFirmwareState(CodeChannel channel) => _processors[(int)channel].FirmwareStackItem;

        /// <summary>
        /// Push a new state on the stack of a given channel procesor. Only to be used by the SPI channel processor!
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="file">Optional file</param>
        /// <returns>Pipeline state</returns>
        internal static Pipelines.PipelineStackItem Push(CodeChannel channel, CodeFile? file = null) => _processors[(int)channel].Push(file);

        /// <summary>
        /// Push a new state on the stack of a given pipeline. Only to be used by the SPI channel processor!
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="macro">Optional macro file</param>
        /// <returns>Pipeline state</returns>
        internal static void Pop(CodeChannel channel) => _processors[(int)channel].Pop();

        /// <summary>
        /// Assign the job file to the given channel. Only used by the job tasks!
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="file">Job file</param>
        internal static void SetJobFile(CodeChannel channel, CodeFile? file) => _processors[(int)channel].SetJobFile(file);

        /// <summary>
        /// Wait for all pending codes to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static Task<bool> FlushAsync(CodeChannel channel, bool flushAll = false) => _processors[(int)channel].FlushAsync(flushAll);

        /// <summary>
        /// Wait for all pending codes of the given file to finish
        /// </summary>
        /// <param name="file">Code file</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static Task<bool> FlushAsync(CodeFile file) => _processors[(int)file.Channel].FlushAsync(file);

        /// <summary>
        /// Wait for all pending codes on the same stack level as the given code to finish.
        /// By default this replaces all expressions as well for convenient parsing by the code processors.
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <param name="syncFileStreams">Whether the file streams are supposed to be synchronized (if applicable)</param>
        /// <param name="ifExecuting">Return true only if the corresponding code input is actually active (ignored if syncFileStreams is true)</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static async Task<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool evaluateAll = true, bool syncFileStreams = false, bool ifExecuting = true)
        {
            if (code is null)
            {
                throw new ArgumentNullException(nameof(code));
            }

            // Wait for the pending codes on this channel to go
            if (!await _processors[(int)code.Channel].FlushAsync(code, evaluateExpressions, evaluateAll))
            {
                return false;
            }

            if (syncFileStreams && code.IsFromFileChannel)
            {
                // Wait for both file streams to reach the same position
                if (await JobProcessor.DoSync(code))
                {
                    await code.UpdateNextFilePositionAsync();
                    return true;
                }
                return false;
            }
            else if (ifExecuting)
            {
                // Make sure the current code channel is executing G/M/T-codes
                using (await Model.Provider.AccessReadOnlyAsync(code.CancellationToken))
                {
                    if (Model.Provider.Get.Inputs[code.Channel]?.Active != true)
                    {
                        return false;
                    }
                }
            }

            // Done
            await code.UpdateNextFilePositionAsync();
            return true;
        }

        /// <summary>
        /// Start the execution of a given code
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public static async ValueTask StartCodeAsync(Commands.Code code)
        {
            ChannelProcessor processor = _processors[(int)code.Channel];
            PipelineStage stage = PipelineStage.Start;

            // Deal with priority codes
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                // Check if the code has to be moved to another channel first
                if (processor.IsIdle(code))
                {
                    await processor.WriteCodeAsync(code, stage);
                    return;
                }

                // Move priority codes to an empty code channel (if possible)
                for (int input = 0; input < Inputs.Total; input++)
                {
                    if ((CodeChannel)input != code.Channel)
                    {
                        ChannelProcessor next = _processors[input];
                        if (next.IsIdle(code))
                        {
                            code.Channel = (CodeChannel)input;
                            await next.WriteCodeAsync(code, stage);
                            return;
                        }
                    }
                }

                // Log a warning if that failed
                processor.Logger.Warn("Failed to move priority code {0} to an empty code channel because all of them are occupied", code);
            }

            // Deal with codes from code interceptors
            Commands.Code? codeBeingIntercepted = IPC.Processors.CodeInterception.GetCodeBeingIntercepted(code.Connection, out InterceptionMode mode);
            if (codeBeingIntercepted is not null)
            {
                // Make sure new codes from macros go the same route as regular macro codes
                if (code.Channel == codeBeingIntercepted.Channel && codeBeingIntercepted.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    code.Flags |= CodeFlags.IsFromMacro;
                    code.File = codeBeingIntercepted.File;
                }

                // Skip start or pre stage if a new code from an active interception targets the same channel. That stage may be blocking when we get here
                if (codeBeingIntercepted.Channel == code.Channel)
                {
                    stage = (mode == InterceptionMode.Pre) ? PipelineStage.ProcessInternally : PipelineStage.Pre;
                }
            }

            // Forward the code to the requested pipeline
            code.Stage = stage;
            await processor.WriteCodeAsync(code, stage);
        }

        /// <summary>
        /// Cancel a given code
        /// </summary>
        /// <param name="code">Code to cancel</param>
        /// <param name="e">Optional exception causing the cancellation</param>
        public static void CancelCode(Commands.Code code, Exception? e = null)
        {
            code.Result = null;
            if (e is not null and not OperationCanceledException)
            {
                code.SetException(e);
            }
            CodeCompleted(code);
        }

        /// <summary>
        /// Execute a given code on a given pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <param name="stage">Stage level to enqueue it at</param>
        public static void CodeCompleted(Commands.Code code) => _processors[(int)code.Channel].WriteCode(code, PipelineStage.Executed);
    }
}
