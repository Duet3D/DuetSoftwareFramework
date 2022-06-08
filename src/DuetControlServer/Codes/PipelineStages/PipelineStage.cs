using DuetControlServer.FileExecution;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Base class for a code pipeline
    /// </summary>
    public abstract class PipelineStage
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="stage">Stage type</param>
        /// <param name="pipeline">Corresponding pipeline</param>
        public PipelineStage(Codes.PipelineStage stage, Pipeline pipeline)
        {
            Stage = stage;
            Pipeline = pipeline;

            Push(null);
        }

        /// <summary>
        /// Stage of this instance
        /// </summary>
        public readonly Codes.PipelineStage Stage;

        /// <summary>
        /// Corresponding pipeline
        /// </summary>
        public readonly Pipeline Pipeline;

        /// <summary>
        /// Stacks holding state information per input channel
        /// </summary>
        protected readonly Stack<PipelineState> _states = new();

        /// <summary>
        /// Get the current state. Should be used only on initialization
        /// </summary>
        internal PipelineState State => _states.Peek();

        /// <summary>
        /// Get the diagnostics from this pipeline stage
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        /// <exception cref="NotImplementedException"></exception>
        public void Diagnostics(StringBuilder builder)
        {
            bool writingDiagnostics = false;
            int numIdleLevels = 0;
            lock (_states)
            {
                foreach (PipelineState state in _states)
                {
                    lock (state)
                    {
                        if (!state.Busy)
                        {
                            numIdleLevels++;
                        }

                        if (state.Busy || writingDiagnostics)
                        {
                            if (!writingDiagnostics)
                            {
                                builder.AppendLine($"{Pipeline.Channel}+{Stage}:");
                                writingDiagnostics = true;
                            }

                            for (int i = 0; i < numIdleLevels; i++)
                            {
                                builder.AppendLine("> Doing nothing");
                            }
                            numIdleLevels = 0;

                            if (state.CodeBeingExecuted != null)
                            {
                                builder.Append($"> Doing {((state.CodeBeingExecuted.Type == DuetAPI.Commands.CodeType.MCode && state.CodeBeingExecuted.MajorNumber == 122) ? "M122" : state.CodeBeingExecuted)}");
                            }
                            if (state.Macro != null)
                            {
                                builder.Append($" from macro {Path.GetFileName(state.Macro.FileName)}");
                            }
                            if (state.PendingCodes.Reader.TryPeek(out _))
                            {
                                if (state.PendingCodes.Reader.CanCount)
                                {
                                    builder.Append($", {state.PendingCodes.Reader.Count} more codes pending");
                                }
                                else
                                {
                                    builder.Append(", more codes pending");
                                }
                            }
                            builder.AppendLine();
                        }
                        else
                        {
                            numIdleLevels++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Check if this stage is currently idle
        /// </summary>
        /// <param name="code">Optional code requesting the check</param>
        /// <returns>Whether the corresponding state is empty</returns>
        public bool IsIdle(Commands.Code code)
        {
            lock (_states)
            {
                PipelineState topState = _states.Peek();
                return !topState.Busy && (code == null || code.Macro == topState.Macro);
            }
        }

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <returns></returns>
        public Task WaitForIdleAsync(Macro macro, CancellationToken cancellationToken)
        {
            lock (_states)
            {
                foreach (PipelineState state in _states)
                {
                    if (state.Macro == macro)
                    {
                        return state.WaitForIdleAsync(cancellationToken);
                    }
                }

                Pipeline.Logger.Warn("Failed to find corresponding state for wait request, falling back to top state");
                return _states.Peek().WaitForIdleAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Process a code from a given code channel
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public abstract Task ProcessCodeAsync(Commands.Code code);

        /// <summary>
        /// Execute a given code on this pipeline stage.
        /// This should not be used unless the corresponding code channel is unbounded
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        public virtual void WriteCode(Commands.Code code) => throw new NotSupportedException();

        /// <summary>
        /// Execute a given code on this pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public virtual ValueTask WriteCodeAsync(Commands.Code code)
        {
            lock (_states)
            {
                foreach (PipelineState state in _states)
                {
                    if (state.Macro == code.Macro)
                    {
                        return state.PendingCodes.Writer.WriteAsync(code);
                    }
                }
            }

            Pipeline.Logger.Error("Failed to find corresponding state for code {0}, cancelling it", code);
            Processor.CancelCode(code);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Push a new element onto the stack
        /// </summary>
        /// <param name="macro">Macro file or null if waiting for acknowledgment</param>
        internal virtual PipelineState Push(Macro macro)
        {
            PipelineState newState = new(this, macro);
            lock (_states)
            {
                _states.Push(newState);
            }
            return newState;
        }

        /// <summary>
        /// Pop the last element from the stack
        /// </summary>
        /// <exception cref="ArgumentException">Failed to pop last element</exception>
        internal virtual void Pop()
        {
            lock (_states)
            {
                if (_states.Count == 1)
                {
                    throw new ArgumentException($"Stack underrun on pipeline {Pipeline.Channel}");
                }
                _states.Pop().PendingCodes.Writer.Complete();
            }
        }

        /// <summary>
        /// Wait for the processor tasks to complete
        /// </summary>
        /// <returns>Asynchronous tasks</returns>
        internal async Task WaitForCompletionAsync()
        {
            // Wait for the lowest task to be terminated first.
            // No need to use a lock here because it is referenced only once on initialization
            await _states.Peek().ProcessorTask;

            // Wait for the remaining states
            List<Task> tasks = new();
            lock (_states)
            {
                foreach (PipelineState state in _states)
                {
                    tasks.Add(state.ProcessorTask);
                }
            }
            await Task.WhenAll(tasks);
        }
    }
}
