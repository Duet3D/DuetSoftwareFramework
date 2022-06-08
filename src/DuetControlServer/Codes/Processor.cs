using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.FileExecution;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class holding code pipelines per code channel
    /// </summary>
    public static class Processor
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Code pipeline per code channel
        /// </summary>
        private static readonly Pipeline[] _pipelines = new Pipeline[Inputs.Total];

        /// <summary>
        /// Initialize this class
        /// </summary>
        public static void Init()
        {
            for (int input = 0; input < Inputs.Total; input++)
            {
                _pipelines[input] = new Pipeline((CodeChannel)input);
            }
        }

        /// <summary>
        /// Lifecycle of this pipeline
        /// </summary>
        /// <returns></returns>
        public static Task Run() => Task.WhenAll(_pipelines.Select(pipeline => pipeline.Run()));

        /// <summary>
        /// Get diagnostics from every pipeline
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        public static void Diagnostics(StringBuilder builder)
        {
            foreach (Pipeline pipeline in _pipelines)
            {
                pipeline.Diagnostics(builder);
            }
        }

        /// <summary>
        /// Get the pipeline state of the firmware stage from a given channel
        /// </summary>
        /// <param name="channel"></param>
        internal static PipelineStages.PipelineState GetFirmwareState(CodeChannel channel) => _pipelines[(int)channel].FirmwareState;

        /// <summary>
        /// Push a new state on the stack of a given pipeline.
        /// Only to be used by the SPI channel processor!
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="macro">Optional macro file</param>
        /// <returns>Pipeline state</returns>
        public static PipelineStages.PipelineState Push(CodeChannel channel, Macro macro = null) => _pipelines[(int)channel].Push(macro);

        /// <summary>
        /// Push a new state on the stack of a given pipeline.
        /// Only to be used by the SPI channel processor!
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <param name="macro">Optional macro file</param>
        /// <returns>Pipeline state</returns>
        public static void Pop(CodeChannel channel) => _pipelines[(int)channel].Pop();

        /// <summary>
        /// Wait for all pending codes to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static Task<bool> FlushAsync(CodeChannel channel) => _pipelines[(int)channel].FlushAsync();

        /// <summary>
        /// Wait for all pending codes on the same stack level as the given code to finish.
        /// By default this replaces all expressions as well for convenient parsing by the code processors.
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static Task<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool evaluateAll = true)
        {
            if (code == null)
            {
                throw new ArgumentNullException(nameof(code));
            }
            return _pipelines[(int)code.Channel].FlushAsync(code, evaluateExpressions, evaluateAll);
        }

        /// <summary>
        /// Start the execution of a given code
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        public static ValueTask StartCodeAsync(Commands.Code code)
        {
            Pipeline pipeline = _pipelines[(int)code.Channel];
            PipelineStage stage = PipelineStage.Start;

            // Deal with priority codes
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                // Check if the code has to be moved to another channel first
                if (pipeline.IsIdle(code))
                {
                    return pipeline.WriteCodeAsync(code, stage);
                }

                // Move priority codes to an empty code channel (if possible)
                foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
                {
                    if (channel != code.Channel)
                    {
                        Pipeline next = _pipelines[(int)channel];
                        if (next.IsIdle(code))
                        {
                            code.Channel = channel;
                            return next.WriteCodeAsync(code, stage);
                        }
                    }
                }

                // Log a warning if that failed
                pipeline.Logger.Warn("Failed to move priority code {0} to an empty code channel because all of them are occupied", code);
            }

            // Deal with codes from code interceptors
            Commands.Code codeBeingIntercepted = IPC.Processors.CodeInterception.GetCodeBeingIntercepted(code.Connection, out InterceptionMode mode);
            if (codeBeingIntercepted != null)
            {
                // Make sure new codes from macros go the same route as regular macro codes
                if (codeBeingIntercepted.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    code.Flags |= CodeFlags.IsFromMacro;
                    code.File = codeBeingIntercepted.File;
                    code.Macro = codeBeingIntercepted.Macro;
                }

                // Skip start or pre stage if a new code from an active interception targets the same channel. That stage may be blocking when we get here
                if (codeBeingIntercepted.Channel == code.Channel)
                {
                    stage = (mode == InterceptionMode.Pre) ? PipelineStage.ProcessInternally : PipelineStage.Pre;
                }
            }

            // Forward the code to the requested pipeline
            code.Stage = stage;
            return pipeline.WriteCodeAsync(code, stage);
        }

        /// <summary>
        /// Cancel a given code
        /// </summary>
        /// <param name="code">Code to cancel</param>
        /// <param name="e">Optional exception causing the cancellation</param>
        public static void CancelCode(Commands.Code code, Exception e = null)
        {
            code.Result = null;
            if (e != null && e is not OperationCanceledException)
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
        public static void CodeCompleted(Commands.Code code) => _pipelines[(int)code.Channel].WriteCode(code, PipelineStage.Executed);
    }
}
