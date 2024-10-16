﻿using DuetControlServer.Commands;
using DuetControlServer.Files;
using Nito.AsyncEx;
using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Class representing an execution level on a given pipeline.
    /// This is the effective target for incoming codes
    /// </summary>
    public class PipelineStackItem
    {
        /// <summary>
        /// Pipeline holding this stack item
        /// </summary>
        private readonly PipelineBase _pipeline;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipeline">Pipeline holding this stack item</param>
        /// <param name="file">Current file or null if not present</param>
        public PipelineStackItem(PipelineBase pipeline, CodeFile? file)
        {
            _pipeline = pipeline;

            if (pipeline.Stage != PipelineStage.Executed)
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
            File = file;

            // Feed incoming codes to the code handler
            if (pipeline.Stage != PipelineStage.Firmware)
            {
                ProcessorTask = Task.Factory.StartNew(async delegate
                {
                    await foreach (Code code in PendingCodes.Reader.ReadAllAsync(Program.CancellationToken))
                    {
                        // Set it up
                        lock (this)
                        {
                            CodeBeingExecuted = code;
                        }
                        code.Stage = pipeline.Stage;

                        // Process it
                        try
                        {
                            if (code.CancellationToken.IsCancellationRequested && pipeline.Stage != PipelineStage.Executed)
                            {
                                // Do not deal with cancelled codes
                                Processor.CancelCode(code);
                            }
                            else
                            {
                                await pipeline.ProcessCodeAsync(code);
                            }
                        }
                        catch (Exception e)
                        {
                            pipeline.Processor.Logger.Error(e, "Failed to process code in stage {0}", pipeline.Stage);
                        }

                        // Code processed, see if there is more to do
                        lock (this)
                        {
                            Busy = PendingCodes.Reader.TryPeek(out _);
                            CodeBeingExecuted = null;
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
        /// Pending codes to be executed
        /// </summary>
        public readonly Channel<Code> PendingCodes;

        /// <summary>
        /// Code file corresponding to this stack item
        /// </summary>
        public CodeFile? File;

        /// <summary>
        /// Internal task processing incoming codes
        /// </summary>
        internal readonly Task ProcessorTask;

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
        /// Current code being executed.
        /// This is not applicable on the Firmware stage because we buffer multiple codes there
        /// </summary>
        public Code? CodeBeingExecuted;

        /// <summary>
        /// Wait for the pipeline state to finish processing codes
        /// </summary>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync()
        {
            if (Program.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await _idleEvent.WaitAsync(Program.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Wait for the pipeline state to finish processing codes
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync(Code code)
        {
            try
            {
                await _idleEvent.WaitAsync(code.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Enqueue a given code on this pipeline state for execution
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        public void WriteCode(Code code)
        {
            lock (this)
            {
                Busy = true;
            }
            if (!PendingCodes.Writer.TryWrite(code))
            {
                _pipeline.Processor.Logger.Error("Pipeline failed to store code immediately so waiting synchronously for it to be added");
                PendingCodes.Writer.WriteAsync(code, Program.CancellationToken).AsTask().Wait();
            }
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
            return PendingCodes.Writer.WriteAsync(code, Program.CancellationToken);
        }
    }
}
