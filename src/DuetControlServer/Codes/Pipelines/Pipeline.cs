using DuetControlServer.Commands;
using DuetControlServer.FileExecution;
using Nito.AsyncEx;
using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Pipeline state for pipeline stages
    /// </summary>
    public class Pipeline
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="processDelegate">Function to invoke on initialization</param>
        /// <param name="macro">Current macro file or null if not present</param>
        /// <param name="unbounded">Whether this state may hold an unlimited number of codes</param>
        public Pipeline(PipelineStage stage, Macro macro)
        {
            if (stage.Stage != Codes.PipelineStage.Executed)
            {
                PendingCodes = Channel.CreateBounded<Code>(new BoundedChannelOptions(Settings.MaxCodesPerInput)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
            }
            else
            {
                PendingCodes = Channel.CreateUnbounded<Code>(new UnboundedChannelOptions()
                {
                    SingleReader = true,
                    SingleWriter = false
                });
            }
            Macro = macro;

            // Feed incoming codes to the code handler
            if (stage.Stage != Codes.PipelineStage.Firmware)
            {
                ProcessorTask = Task.Factory.StartNew(async delegate
                {
                    await foreach (Code code in PendingCodes.Reader.ReadAllAsync(Program.CancellationToken))
                    {
                        if (code.CancellationToken.IsCancellationRequested && stage.Stage != Codes.PipelineStage.Executed)
                        {
                            Processor.CancelCode(code);
                            continue;
                        }

                        lock (this)
                        {
                            CodeBeingExecuted = code;
                        }

                        try
                        {
                            code.Stage = stage.Stage;
                            await stage.ProcessCodeAsync(code);
                        }
                        catch (Exception e)
                        {
                            stage.Pipeline.Logger.Error(e, "Failed to process code in stage {0}", stage.Stage);
                        }
                        finally
                        {
                            lock (this)
                            {
                                Busy = PendingCodes.Reader.TryPeek(out _);
                                CodeBeingExecuted = null;
                            }
                        }
                    }
                }).Unwrap();
            }
            else
            {
                ProcessorTask = Task.CompletedTask;
            }
        }

        /// <summary>
        /// Pending codes per channel
        /// </summary>
        public readonly Channel<Code> PendingCodes;

        /// <summary>
        /// Macro of the corresponding 
        /// </summary>
        public readonly Macro Macro;

        /// <summary>
        /// Internal task processing incoming codes
        /// </summary>
        public readonly Task ProcessorTask;

        /// <summary>
        /// Indicates if the pipeline state is busy processing codes
        /// </summary>
        public bool Busy
        {
            get => !_idleEvent.IsSet;
            set
            {
                if (value)
                {
                    _idleEvent.Reset();
                }
                else
                {
                    _idleEvent.Set();
                }
            }
        }
        private readonly AsyncManualResetEvent _idleEvent = new(true);

        /// <summary>
        /// Current code being executed
        /// </summary>
        public Code CodeBeingExecuted;

        /// <summary>
        /// Wait for the pipeline state to finish processing codes
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync(Code code)
        {
            try
            {
                await _idleEvent.WaitAsync((code != null) ? code.CancellationToken : Program.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Enqueue a given code asynchrously on this pipeline state for execution
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public ValueTask WriteCodeAsync(Code code)
        {
            lock (this)
            {
                Busy = true;
            }
            return PendingCodes.Writer.WriteAsync(code, code.CancellationToken);
        }
    }
}
