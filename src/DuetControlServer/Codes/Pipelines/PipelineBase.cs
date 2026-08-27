using DuetControlServer.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

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
    public readonly ChannelProcessor ChannelProcessor;

    /// <summary>
    /// Code processor
    /// </summary>
    public readonly CodeProcessor CodeProcessor;

    /// <summary>
    /// Application settings
    /// </summary>
    private readonly Settings _settings;

    /// <summary>
    /// Application lifetime
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="stage">Stage type</param>
    /// <param name="channelProcessor">Channel processor</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="lifetime">Application lifetime</param>
    /// <param name="settings">Application settings</param>
    public PipelineBase(PipelineStage stage, ChannelProcessor channelProcessor, CodeProcessor codeProcessor, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
    {
        Stage = stage;
        ChannelProcessor = channelProcessor;

        CodeProcessor = codeProcessor;
        _settings = settings.Value;
        _lifetime = lifetime;

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
    public PipelineStackItem CurrentStackItem => _stack.Peek();

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
                            builder.AppendLine($"{ChannelProcessor.Channel}+{Stage}:");
                            writingDiagnostics = true;
                        }

                        builder.Append(prefix);
                        builder.Append(' ');
                        if (stackItem.File is not null)
                        {
                            builder.Append(stackItem.File is MacroFile ? "Macro " : "File ");
                            builder.Append(stackItem.File.FilePath.Virtual);
                            builder.Append(": ");
                        }

                        if (stackItem.CodeBeingExecuted is not null)
                        {
                            lock (stackItem.CodeBeingExecuted)
                            {
                                builder.Append("Executing ");
                                builder.Append((stackItem.CodeBeingExecuted.Type == DuetAPI.Commands.CodeType.MCode && stackItem.CodeBeingExecuted.MajorNumber == 122) ? "M122" : stackItem.CodeBeingExecuted);
                            }
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
            return (code is null || code.File == CurrentStackItem.File) && !CurrentStackItem.Busy;
        }
    }

    /// <summary>
    /// Wait for the first or current pipeline stack item to become idle
    /// </summary>
    /// <param name="flushAll">Flush everything</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public virtual ValueTask<bool> FlushAsync(bool flushAll, CancellationToken cancellationToken = default) => flushAll ? _baseItem.FlushAsync(cancellationToken) : CurrentStackItem.FlushAsync(cancellationToken);

    /// <summary>
    /// Wait for the pipeline stage to become idle
    /// </summary>
    /// <param name="file">Code file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public virtual ValueTask<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        lock (_stack)
        {
            foreach (PipelineStackItem stackItem in _stack)
            {
                if (stackItem.File == file)
                {
                    return stackItem.FlushAsync(cancellationToken);
                }
            }
            return ValueTask.FromResult(false);
        }
    }

    /// <summary>
    /// Wait for the pipeline stage to become idle
    /// </summary>
    /// <param name="code">Code waiting for the flush</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public virtual ValueTask<bool> FlushAsync(Commands.Code code, CancellationToken cancellationToken = default)
    {
        lock (_stack)
        {
            foreach (PipelineStackItem stackItem in _stack)
            {
                if (stackItem.File == code.File)
                {
                    return stackItem.FlushAsync(cancellationToken);
                }
            }
            return ValueTask.FromResult(false);
        }
    }

    /// <summary>
    /// Process a code from a given code channel
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <returns>Asynchronous task</returns>
    public abstract ValueTask ProcessCodeAsync(Commands.Code code);

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
                if (stackItem.File == code.File)
                {
                    return stackItem.WriteCodeAsync(code);
                }
            }
        }

        ChannelProcessor.Logger.LogError("Failed to find corresponding state for code {Code}, cancelling it", code);
        CodeProcessor.CancelCode(code);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// How many levels this pipeline's stack holds, counting the base level
    /// </summary>
    public int StackDepth
    {
        get
        {
            lock (_stack)
            {
                return _stack.Count;
            }
        }
    }

    /// <summary>
    /// The files this pipeline's stack holds, innermost first
    /// </summary>
    /// <returns>A snapshot of the stack's files</returns>
    /// <remarks>
    /// A snapshot rather than the stack itself, so that a caller deciding what to do about the stack
    /// is not walking it while another thread pushes onto it
    /// </remarks>
    public IReadOnlyList<Files.CodeFile?> StackedFiles()
    {
        lock (_stack)
        {
            return [.. _stack.Select(item => item.File)];
        }
    }

    /// <summary>
    /// A code currently deferred on this pipeline: dispatched without being awaited, its handler
    /// held back until its anchor move retires
    /// </summary>
    /// <param name="Code">The deferred code</param>
    /// <param name="Cts">Cancellation source that cancels this deferred code alone</param>
    /// <param name="Completion">Completion of the code's dispatch, including its onward routing</param>
    private sealed record DeferredCode(Commands.Code Code, CancellationTokenSource Cts, Task Completion);

    /// <summary>
    /// Codes deferred on this pipeline, in dispatch order. Guarded by itself
    /// </summary>
    /// <remarks>
    /// The set belongs to the pipeline rather than to a stack level, as RepRapFirmware's queued
    /// codes belong to the channel rather than to the macro that produced them: a macro may finish
    /// and pop while a code it deferred is still owed, and the code must stay visible to the
    /// standstill wait and to purge cancellation. Deferred codes are excluded from the stack items'
    /// <see cref="PipelineStackItem.Busy"/>, so flushes and the waits that pop a finished file do
    /// not wait for them; the standstill wait counts them through
    /// <see cref="CodeProcessor.WaitForStandstillAsync"/> instead. That is the split of the two
    /// pending predicates
    /// </remarks>
    private readonly List<DeferredCode> _deferredCodes = [];

    /// <summary>
    /// Dispatch a deferred code without awaiting it
    /// </summary>
    /// <param name="code">Code to defer</param>
    /// <param name="ring">Ring its anchor was queued on</param>
    /// <param name="anchor">Id of its anchor move</param>
    /// <remarks>
    /// The code gets a cancellation source of its own, detached from the channel's: a pause
    /// cancels the channel's pending codes wholesale, but a deferred code whose anchor was not
    /// purged is owed and must survive that. It is cancelled selectively instead, by
    /// <see cref="CancelDeferredCodesAfter"/> when a feedhold purges its anchor and by
    /// <see cref="CancelAllDeferredCodes"/> when everything pending is discarded. Each deferred code
    /// chains on the one deferred before it, so effects land in file order even when they share an
    /// anchor
    /// </remarks>
    public void DeferCode(Commands.Code code, int ring, uint anchor)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DeferredCode deferred = new(code, cts, completion.Task);

        code.IsCurrentlyDeferred = true;
        code.DeferredRing = ring;
        code.DeferredAnchor = anchor;
        code.CancellationToken = cts.Token;
        lock (_deferredCodes)
        {
            code.DeferredPredecessor = _deferredCodes.Count > 0 ? _deferredCodes[^1].Completion : null;
            _deferredCodes.Add(deferred);
        }
        _ = RunDeferredCodeAsync(deferred, completion);
    }

    private async Task RunDeferredCodeAsync(DeferredCode deferred, TaskCompletionSource completion)
    {
        try
        {
            await ProcessCodeAsync(deferred.Code);
        }
        catch (Exception e)
        {
            // ProcessCodeAsync handles its own errors; nothing may escape an unawaited task
            ChannelProcessor.Logger.LogError(e, "Failed to process deferred code {Code}", deferred.Code);
        }
        finally
        {
            lock (_deferredCodes)
            {
                _deferredCodes.Remove(deferred);
                deferred.Cts.Dispose();
            }
            completion.TrySetResult();
        }
    }

    /// <summary>
    /// Completion of the last code currently deferred on this pipeline, or null if none is
    /// </summary>
    /// <remarks>
    /// Deferred codes chain on their predecessors, so the last one's completion is the whole set's
    /// </remarks>
    public Task? LastDeferredCodeTask()
    {
        lock (_deferredCodes)
        {
            return _deferredCodes.Count > 0 ? _deferredCodes[^1].Completion : null;
        }
    }

    /// <summary>
    /// Cancel every deferred code whose anchor is at or past the given move id
    /// </summary>
    /// <param name="firstPurgedMoveId">Id of the earliest move a feedhold purged</param>
    /// <remarks>
    /// The purge boundary and the job's rewind point are the same number, so the cancelled codes
    /// are exactly the ones the replay re-reads: each deferred code fires once, on whichever side
    /// of the pause it ends up
    /// </remarks>
    public void CancelDeferredCodesAfter(uint firstPurgedMoveId)
        => Cancel(deferred => (int)(deferred.Code.DeferredAnchor - firstPurgedMoveId) >= 0);

    /// <summary>
    /// Cancel every deferred code on this pipeline
    /// </summary>
    public void CancelAllDeferredCodes() => Cancel(_ => true);

    /// <summary>
    /// Cancel the deferred codes a predicate picks out
    /// </summary>
    /// <param name="shouldCancel">Which of them to cancel</param>
    /// <remarks>
    /// The set to cancel is taken under the lock and cancelled outside it. Cancelling runs the
    /// waiting code's continuation on this thread, and what that code does as it unwinds is remove
    /// itself from this list - so cancelling while iterating it would throw, part way through, out
    /// of whatever asked for the cancellation. For the pause that is the sequence that puts the
    /// machine down, which would be left half done with the job still holding the codes it was
    /// waiting for.
    /// <para>
    /// A source already disposed belongs to a code that has just finished on another thread, which
    /// is the outcome asked for here
    /// </para>
    /// </remarks>
    private void Cancel(Func<DeferredCode, bool> shouldCancel)
    {
        DeferredCode[] toCancel;
        lock (_deferredCodes)
        {
            toCancel = _deferredCodes.Where(shouldCancel).ToArray();
        }

        foreach (DeferredCode deferred in toCancel)
        {
            try
            {
                deferred.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The code finished as this was deciding to cancel it
            }
        }
    }

    /// <summary>
    /// Push a new element onto the stack
    /// </summary>
    /// <param name="file">Code file or null if waiting for acknowledgment</param>
    public virtual PipelineStackItem Push(CodeFile? file)
    {
        PipelineStackItem newState = new(this, file, CodeProcessor, _settings, _lifetime);
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
    public virtual void Pop()
    {
        lock (_stack)
        {
            if (_stack.Count == 1)
            {
                throw new ArgumentException($"Stack underrun on pipeline {ChannelProcessor.Channel}");
            }
            _stack.Pop().PendingCodes.Writer.Complete();
        }
    }

    /// <summary>
    /// Set the job file
    /// </summary>
    public void SetJobFile(CodeFile? file)
    {
        lock (_stack)
        {
            _baseItem.File = file;
        }
    }

    /// <summary>
    /// Check if the pipeline has a valid job file
    /// </summary>
    public bool HasValidJobFile
    {
        get
        {
            lock (_stack)
            {
                // The first stack item may only hold a job file and never a macro file...
                return _baseItem.File is not null && !_baseItem.File.IsClosed;
            }
        }
    }

    /// <summary>
    /// Wait for the processor tasks to complete
    /// </summary>
    /// <returns>Asynchronous tasks</returns>
    public async Task WaitForCompletionAsync()
    {
        // Wait for the lowest task to be terminated first.
        // No need to use a lock here because it is referenced only once on initialization
        await _stack.Peek().ProcessorTask;

        // Wait for the remaining states
        List<Task> tasks = [];
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
