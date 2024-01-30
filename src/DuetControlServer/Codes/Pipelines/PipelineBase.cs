using DuetControlServer.FileExecution;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            _baseItem = Push(null);
        }

        /// <summary>
        /// Stacks holding state information per input channel
        /// </summary>
        protected readonly Stack<PipelineStackItem> _stack = new();

        /// <summary>
        /// Base state of this pipeline
        /// </summary>
        protected readonly PipelineStackItem _baseItem;

        /// <summary>
        /// Current item on the stack
        /// </summary>
        internal PipelineStackItem CurrentStackItem => _stack.Peek();

        /// <summary>
        /// Get the diagnostics from this pipeline stage
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        /// <exception cref="NotImplementedException"></exception>
        public void Diagnostics(StringBuilder builder)
        {
            bool writingDiagnostics = false;

            string prefix = ">";
            lock (_stack)
            {
                // Print diagnostics for stack from bottom to top
                foreach (PipelineStackItem stackItem in _stack.Reverse())
                {
                    lock (stackItem)
                    {
                        if (stackItem.Busy || writingDiagnostics)
                        {
                            if (!writingDiagnostics)
                            {
                                builder.AppendLine($"{Processor.Channel}+{Stage}:");
                                writingDiagnostics = true;
                            }

                            builder.Append(prefix);
                            builder.Append(' ');
                            if (stackItem.Macro is not null)
                            {
                                builder.Append("Macro ");
                                builder.Append(Path.GetFileName(stackItem.Macro.FileName));
                                builder.Append(": ");
                            }

                            if (stackItem.CodeBeingExecuted is not null)
                            {
                                builder.Append("Executing ");
                                builder.Append((stackItem.CodeBeingExecuted.Type == DuetAPI.Commands.CodeType.MCode && stackItem.CodeBeingExecuted.MajorNumber == 122) ? "M122" : stackItem.CodeBeingExecuted);
                            }
                            else if (stackItem.Busy)
                            {
                                builder.Append("Busy");
                            }
                            else
                            {
                                builder.Append("Idle");
                            }

                            if (stackItem.PendingCodes.Reader.CanCount && stackItem.PendingCodes.Reader.Count > 0)
                            {
                                builder.Append(" (");
                                builder.Append(stackItem.PendingCodes.Reader.Count);
                                builder.AppendLine(" more codes pending)");
                            }
                            else
                            {
                                builder.AppendLine();
                            }
                        }
                    }
                    prefix += '>';
                }
            }
        }

        /// <summary>
        /// Check if this stage is currently idle
        /// </summary>
        /// <param name="code">Optional code requesting the check</param>
        /// <returns>Whether this pipeline stage is idle</returns>
        public bool IsIdle(Commands.Code? code)
        {
            lock (_stack)
            {
                PipelineStackItem topState = _stack.Peek();
                return (code is null || code.Macro == topState.Macro) && !topState.Busy;
            }
        }

        /// <summary>
        /// Wait for the current pipeline stack item to become idle
        /// </summary>
        public virtual Task<bool> FlushAsync() => CurrentStackItem.FlushAsync();

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
                foreach (PipelineStackItem stackItem in _stack)
                {
                    if (stackItem.Macro == code.Macro)
                    {
                        return stackItem.FlushAsync(code);
                    }
                }

                Processor.Logger.Warn("Failed to find corresponding state for code flush request, falling back to top state");
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
        /// Enqueue a given code asynchronously on this pipeline state for execution
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public virtual ValueTask WriteCodeAsync(Commands.Code code)
        {
            lock (_stack)
            {
                foreach (PipelineStackItem stackItem in _stack)
                {
                    if (stackItem.Macro == code.Macro)
                    {
                        return stackItem.WriteCodeAsync(code);
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
        internal virtual PipelineStackItem Push(Macro? macro)
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
                foreach (PipelineStackItem stackItem in _stack)
                {
                    tasks.Add(stackItem.ProcessorTask);
                }
            }
            await Task.WhenAll(tasks);
        }
    }
}
