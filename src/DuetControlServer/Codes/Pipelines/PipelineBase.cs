using DuetControlServer.FileExecution;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Abstract base class for pipeline elements
    /// </summary>
    public abstract class PipelineBase
    {
        /// <summary>
        /// Stage of this instance
        /// </summary>
        public readonly PipelineStage Stage;

        /// <summary>
        /// Corresponding channel processor
        /// </summary>
        public readonly ChannelProcessor Processor;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="stage">Stage type</param>
        /// <param name="processor">Channel processor</param>
        public PipelineBase(PipelineStage stage, ChannelProcessor processor)
        {
            Stage = stage;
            Processor = processor;

            // Make sure there is at least one item on the stack...
            _baseState = Push(null);
        }

        /// <summary>
        /// Stacks holding state information per input channel
        /// </summary>
        protected readonly Stack<PipelineStackItem> _stack = new();

        /// <summary>
        /// Base state of this pipeline
        /// </summary>
        protected readonly PipelineStackItem _baseState;

        /// <summary>
        /// Get the current state. Should be used only on initialization
        /// </summary>
        internal PipelineStackItem State => _stack.Peek();

        /// <summary>
        /// Get the diagnostics from this pipeline stage
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        /// <exception cref="NotImplementedException"></exception>
        public void Diagnostics(StringBuilder builder)
        {
            bool writingDiagnostics = false;
            int numIdleLevels = 0;
            lock (_stack)
            {
                foreach (PipelineStackItem state in _stack)
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
                                builder.AppendLine($"{Processor.Channel}+{Stage}:");
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
            lock (_stack)
            {
                PipelineStackItem topState = _stack.Peek();
                return !topState.Busy && (code == null || code.Macro == topState.Macro);
            }
        }

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        public virtual Task<bool> FlushAsync()
        {
            return _baseState.FlushAsync();
        }

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public virtual Task<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool evaluateAll = true)
        {
            lock (_stack)
            {
                foreach (PipelineStackItem state in _stack)
                {
                    if (state.Macro == code.Macro)
                    {
                        return state.FlushAsync(code);
                    }
                }

                Processor.Logger.Warn("Failed to find corresponding state for flush request, falling back to top state");
                return _stack.Peek().FlushAsync(code);
            }
        }

        /// <summary>
        /// Process a code from a given code channel
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public abstract Task ProcessCodeAsync(Commands.Code code);

        /// <summary>
        /// Enqueue a given code on this pipeline state for execution.
        /// This should not be used unless the corresponding code channel is unbounded
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        public virtual void WriteCode(Commands.Code code) => throw new NotSupportedException();

        /// <summary>
        /// Enqueue a given code asynchronousl;y on this pipeline state for execution
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public virtual ValueTask WriteCodeAsync(Commands.Code code)
        {
            lock (_stack)
            {
                foreach (PipelineStackItem state in _stack)
                {
                    if (state.Macro == code.Macro)
                    {
                        return state.WriteCodeAsync(code);
                    }
                }
            }

            Processor.Logger.Error("Failed to find corresponding state for code {0}, cancelling it", code);
            Codes.Processor.CancelCode(code);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Push a new element onto the stack
        /// </summary>
        /// <param name="macro">Macro file or null if waiting for acknowledgment</param>
        internal virtual PipelineStackItem Push(Macro macro)
        {
            PipelineStackItem newState = new(this, macro);
            lock (_stack)
            {
                _stack.Push(newState);
            }
            return newState;
        }

        /// <summary>
        /// Pop the last element from the stack
        /// </summary>
        /// <exception cref="ArgumentException">Failed to pop last element</exception>
        internal virtual void Pop()
        {
            lock (_stack)
            {
                if (_stack.Count == 1)
                {
                    throw new ArgumentException($"Stack underrun on pipeline {Processor.Channel}");
                }
                _stack.Pop().PendingCodes.Writer.Complete();
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
            await _stack.Peek().ProcessorTask;

            // Wait for the remaining states
            List<Task> tasks = new();
            lock (_stack)
            {
                foreach (PipelineStackItem state in _stack)
                {
                    tasks.Add(state.ProcessorTask);
                }
            }
            await Task.WhenAll(tasks);
        }
    }
}
