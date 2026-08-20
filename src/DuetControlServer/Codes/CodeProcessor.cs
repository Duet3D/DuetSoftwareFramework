using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Files;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes;

/// <summary>
/// Main class delegating parallel G/M/T-code execution
/// </summary>
/// <param name="expressions">Expression parser</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="serviceProvider">Service provider</param>
[DiagnosticsPriority(0)]
public sealed class CodeProcessor(Expressions expressions, Model.ObjectModel model, IHostApplicationLifetime lifetime, IServiceProvider serviceProvider) : IDiagnostics
{
    /// <summary>
    /// Lock around the files being written
    /// </summary>
    public readonly AsyncLock[] FileLocks = [.. Enum.GetValues<CodeChannel>().Select(channel => new AsyncLock()).ToArray()];

    /// <summary>
    /// Current stream writer of the files being written to (M28/M29)
    /// </summary>
    public readonly StreamWriter?[] FilesBeingWritten = new StreamWriter[Inputs.Total];

    /// <summary>
    /// Processors per code channel
    /// </summary>
    public readonly Lazy<ChannelProcessor[]> Processors = new(() => [.. Enum.GetValues<CodeChannel>().Select(channel => ActivatorUtilities.CreateInstance<ChannelProcessor>(serviceProvider, channel)) ]);

    /// <summary>
    /// Get diagnostics from every channel processor
    /// </summary>
    /// <param name="builder">String builder to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        foreach (ChannelProcessor processor in Processors.Value)
        {
            processor.Diagnostics(builder);
        }
    }

    /// <summary>
    /// Push a new state on the stack of a given channel processor
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="file">Optional file</param>
    public void Push(CodeChannel channel, CodeFile? file = null) => Processors.Value[(int)channel].Push(file);

    /// <summary>
    /// Pop the last state from the stack of a given channel processor
    /// </summary>
    /// <param name="channel">Code channel</param>
    public void Pop(CodeChannel channel) => Processors.Value[(int)channel].Pop();

    /// <summary>
    /// How many stack levels a channel has pushed, counting the base level
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>Stack depth</returns>
    public int GetStackDepth(CodeChannel channel) => Processors.Value[(int)channel].StackDepth;

    /// <summary>
    /// The file the given channel is currently executing, if any
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>The file on top of the channel's stack, or null if it is not running one</returns>
    public CodeFile? GetCurrentFile(CodeChannel channel) => Processors.Value[(int)channel].CurrentFile;

    /// <summary>
    /// Abort every file running on a code channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// Aborting used to be driven by the firmware telling us it had abandoned its own stack, and this
    /// side followed. There is no second stack to agree with now: the files here are the only ones
    /// there are, so aborting them is the whole operation
    /// </remarks>
    public async Task AbortAllFilesAsync(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        await Processors.Value[(int)channel].AbortAllFilesAsync(cancellationToken);

        if (channel is CodeChannel.File or CodeChannel.File2)
        {
            JobProcessor jobProcessor = serviceProvider.GetRequiredService<JobProcessor>();
            using (await jobProcessor.LockAsync(cancellationToken))
            {
                jobProcessor.Abort();
            }
        }
    }

    /// <summary>
    /// Whether every macro running on a channel can be restarted from its beginning
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>True if no macro on the channel refuses to be interrupted</returns>
    public bool CanRestartMacros(CodeChannel channel) => Processors.Value[(int)channel].CanRestartMacros;

    /// <summary>
    /// Whether a channel is running any macro at all
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>True if a macro is on its stack</returns>
    public bool IsDoingMacro(CodeChannel channel) => Processors.Value[(int)channel].IsDoingMacro;

    /// <summary>
    /// Abandon the macros a pause interrupts on a channel, leaving its job file in place
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task AbandonMacrosForPauseAsync(CodeChannel channel, CancellationToken cancellationToken = default)
        => Processors.Value[(int)channel].AbandonMacrosForPauseAsync(cancellationToken);

    /// <summary>
    /// Assign the job file to the given channel. Only used by the job tasks!
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="file">Job file</param>
    public void SetJobFile(CodeChannel channel, CodeFile? file) => Processors.Value[(int)channel].SetJobFile(file);

    /// <summary>
    /// Wait for all pending codes to finish
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <param name="flushAll">Flush all codes on all stack levels</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public ValueTask<bool> FlushAsync(CodeChannel channel, bool flushAll = false, CancellationToken cancellationToken = default) => Processors.Value[(int)channel].FlushAsync(flushAll, cancellationToken);

    /// <summary>
    /// Wait for all pending codes of the given file to finish
    /// </summary>
    /// <param name="file">Code file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public ValueTask<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default) => Processors.Value[(int)file.Channel].FlushAsync(file, cancellationToken);

    /// <summary>
    /// Wait for the codes ahead of the given one on its stack level to finish their remaining
    /// pipeline stages. Execution itself is serial per stage, so this orders the caller against
    /// what completes asynchronously behind it: codes a plugin is still executing, replies and log
    /// output emitted at the Executed stage, the meta G-code <c>result</c>, and file positions.
    /// By default this evaluates the code's expressions afterwards, so they see settled state.
    /// </summary>
    /// <param name="code">Code waiting for the flush</param>
    /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
    /// <param name="syncFileStreams">Whether the file streams are supposed to be synchronized (if applicable)</param>
    /// <param name="ifExecuting">Return true only if the corresponding code input is actually active (ignored if syncFileStreams is true)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async ValueTask<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool syncFileStreams = false, bool ifExecuting = true, CancellationToken cancellationToken = default)
    {
        // Wait for the pending codes on this channel to go
        if (!await Processors.Value[(int)code.Channel].FlushAsync(code, cancellationToken))
        {
            return false;
        }

        // See if any expressions need to be evaluated
        if (evaluateExpressions)
        {
            // Code is about to be processed internally, evaluate potential expressions
            await expressions.EvaluateAsync(code, cancellationToken);
        }

        if (syncFileStreams && code.IsFromFileChannel)
        {
            // Wait for both file streams to reach the same position
            if (await DoSyncAsync(code, cancellationToken))
            {
                await code.UpdateNextFilePositionAsync(cancellationToken);
                return true;
            }
            return false;
        }
        else if (ifExecuting && model.MultipleMotionSystemsConfigured)
        {
            // Make sure the current code channel is executing G/M/T-codes.
            // This check is only needed with multiple motion systems where a channel
            // may be inactive because it belongs to a different motion system
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (model.Inputs[code.Channel]?.Active != true)
                {
                    return false;
                }
            }
        }

        // Done
        await code.UpdateNextFilePositionAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Motion planner, resolved lazily because it is built after the code processor
    /// </summary>
    private Motion.MovePlanner? _planner;

    /// <summary>
    /// Wait for the machine to come to a standstill, as a Barrier-class code requires before its
    /// handler runs
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True when the machine is at a standstill, false when cancelled</returns>
    public ValueTask<bool> WaitForStandstillAsync(CancellationToken cancellationToken = default)
        => (_planner ??= serviceProvider.GetRequiredService<Motion.MovePlanner>()).WaitForStandstillAsync(cancellationToken);

    /// <summary>
    /// Start the execution of a given code
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask StartCodeAsync(Commands.Code code)
    {
        ChannelProcessor processor = Processors.Value[(int)code.Channel];
        PipelineStage stage = PipelineStage.Start;

        // Deal with priority codes
        if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
        {
            // Process this priority code here if it is idle or from the firmware (firmware codes must not change channel)
            if (code.Flags.HasFlag(CodeFlags.IsFromFirmware) || processor.IsIdle(code))
            {
                await processor.WriteCodeAsync(code, stage);
                return;
            }

            // Otherwise move it to another idle code channel with the same emulation type (if possible)
            using (await model.AccessReadOnlyAsync())
            {
                Compatibility compatibility = model.Inputs[code.Channel]?.Compatibility ?? Compatibility.RepRapFirmware;
                for (int input = 0; input < Inputs.Total; input++)
                {
                    CodeChannel channel = (CodeChannel)input;
                    if (channel != code.Channel && channel is not CodeChannel.File and not CodeChannel.File2)
                    {
                        ChannelProcessor next = Processors.Value[input];
                        if (model.Inputs[channel]?.Compatibility == compatibility && next.IsIdle(code))
                        {
                            code.Channel = channel;
                            await next.WriteCodeAsync(code, stage); // This can't block if the channel is idle
                            return;
                        }
                    }
                }
            }

            // Otherwise move it to an arbitrary idle code channel (if possible)
            for (int input = 0; input < Inputs.Total; input++)
            {
                CodeChannel channel = (CodeChannel)input;
                if (channel != code.Channel && channel is not CodeChannel.File and not CodeChannel.File2)
                {
                    ChannelProcessor next = Processors.Value[input];
                    if (next.IsIdle(code))
                    {
                        code.Channel = channel;
                        await next.WriteCodeAsync(code, stage);
                        return;
                    }
                }
            }

            // Log a warning if that failed
            processor.Logger.LogWarning("Failed to move priority code {Code} to an empty code channel because all of them are occupied", code);
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

        // Forward the code to the requested pipeline.
        // If it is marked to bypass the internal processing, send it straight to the Post stage to avoid
        // a potential deadlock when this code is started from within an internal processing handler
        if (code.Flags.HasFlag(CodeFlags.IsInternallyProcessed))
        {
            stage = PipelineStage.Post;
        }
        code.Stage = stage;
        await processor.WriteCodeAsync(code, stage);
    }

    /// <summary>
    /// Cancel a given code
    /// </summary>
    /// <param name="code">Code to cancel</param>
    /// <param name="e">Optional exception causing the cancellation</param>
    public void CancelCode(Commands.Code code, Exception? e = null)
    {
        code.Result = null;
        if (e is not null and not OperationCanceledException)
        {
            code.SetException(e);
        }
        CodeCompleted(code);
    }

    /// <summary>
    /// List of cancellation tokens to cancel pending codes while they are waiting for their execution
    /// </summary>
    /// <remarks>
    /// While it may appear nicer to move the cancellation functionality to the code pipeline itself,
    /// this coule lead to performance issues or unexpected behaviour due to intercepted codes. So leave it here for now
    /// </remarks>
    public readonly CancellationTokenSource[] CancellationTokenSources = [.. Enum.GetValues<CodeChannel>().Select(channel => CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping)) ];

    /// <summary>
    /// Cancel pending codes of the given channel
    /// </summary>
    /// <param name="channel">Channel to cancel codes from</param>
    public void CancelPending(CodeChannel channel)
    {
        lock (CancellationTokenSources)
        {
            // Cancel and dispose the existing CTS
            CancellationTokenSource oldTcs = CancellationTokenSources[(int)channel];
            oldTcs.Cancel();
            oldTcs.Dispose();

            // Create a new one
            CancellationTokenSources[(int)channel] = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        }
    }

    /// <summary>
    /// Execute a given code on a given pipeline stage
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    public void CodeCompleted(Commands.Code code) => Processors.Value[(int)code.Channel].WriteCode(code, PipelineStage.Executed);

    /// <summary>
    /// Dictionary of codes vs. synchronization tasks
    /// </summary>
    private readonly Dictionary<Commands.Code, TaskCompletionSource<bool>> _syncRequests = [];

    /// <summary>
    /// Synchronize the File and File2 code streams, may only be called when a job is live
    /// </summary>
    /// <param name="code">Code to synchronize at</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the sync request was successful, false otherwise</returns>
    /// <remarks>
    /// This must be called while the Job class is NOT locked and it must be called from the same
    /// code on File *AND* File2, else the sync request is never resolved (or at least not before the file is cancelled)
    /// </remarks>
    public async Task<bool> DoSyncAsync(Commands.Code code, CancellationToken cancellationToken = default)
    {
        if (!code.IsFromFileChannel)
        {
            throw new ArgumentException("Code is not from a file channel");
        }
        if (code.FilePosition is null)
        {
            throw new ArgumentException("Code has no file position and cannot be used for sync requests", nameof(code));
        }

        if (!Processors.Value[(int)CodeChannel.File].HasValidJobFile || !Processors.Value[(int)CodeChannel.File2].HasValidJobFile)
        {
            // There is nothing to sync if the files have finished or if there is only one file stream...
            return true;
        }

        Task<bool> syncTask;
        lock (_syncRequests)
        {
            foreach (Commands.Code item in _syncRequests.Keys)
            {
                if (code.Channel != item.Channel && code.FilePosition == item.FilePosition)
                {
                    _syncRequests[item].TrySetResult(true);
                    return true;
                }
            }

            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _syncRequests.Add(code, tcs);
            syncTask = tcs.Task;
        }
        return await syncTask;
    }

    /// <summary>
    /// Resolve all sync requests that are after the given file position
    /// </summary>
    /// <param name="filePosition">File position</param>
    public void ResolveSyncRequestsAfter(long filePosition)
    {
        lock (_syncRequests)
        {
            foreach (Commands.Code code in _syncRequests.Keys.ToList())
            {
                if (code.FilePosition >= filePosition)
                {
                    _syncRequests[code].TrySetResult(false);
                    _syncRequests.Remove(code);
                }
            }
        }
    }

    /// <summary>
    /// Resolve all sync requests for a given file
    /// </summary>
    /// <param name="file">File</param>
    public void PurgeSyncRequestsFor(CodeFile file)
    {
        // Reset the job file of the channel; we only get here when it is done. Do this to avoid race conditions
        SetJobFile(file.Channel, null);

        // Remove all sync requests for this file
        lock (_syncRequests)
        {
            foreach (Commands.Code syncingCode in _syncRequests.Keys.ToArray())
            {
                if (syncingCode.File != file)
                {
                    _syncRequests[syncingCode].TrySetResult(false);
                    _syncRequests.Remove(syncingCode);
                }
            }
        }
    }
}
