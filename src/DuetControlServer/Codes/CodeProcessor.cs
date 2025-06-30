using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// <param name="model">Object model</param>
[DiagnosticsPriority(-10)]
public sealed class CodeProcessor : IDiagnostics
{
    // Private fields
    private readonly Model.ObjectModel _model;
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Lock around the files being written
    /// </summary>
    public readonly AsyncLock[] FileLocks = new AsyncLock[Inputs.Total];

    /// <summary>
    /// Current stream writer of the files being written to (M28/M29)
    /// </summary>
    public readonly StreamWriter?[] FilesBeingWritten = new StreamWriter[Inputs.Total];

    /// <summary>
    /// Processors per code channel
    /// </summary>
    public readonly ChannelProcessor[] Processors = new ChannelProcessor[Inputs.Total];

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="model">Object model</param>
    /// <param name="serviceProvider">Service provider</param>
    /// <param name="lifetime">Application lifetime</param>
    public CodeProcessor(Model.ObjectModel model, IServiceProvider serviceProvider, IHostApplicationLifetime lifetime)
    {
        _model = model;
        _lifetime = lifetime;
        foreach (CodeChannel channel in Enum.GetValues<CodeChannel>())
        {
            Processors[(int)channel] = ActivatorUtilities.CreateInstance<ChannelProcessor>(serviceProvider, channel, this);
        }
    }

    /// <summary>
    /// Get diagnostics from every channel processor
    /// </summary>
    /// <param name="builder">String builder to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        foreach (ChannelProcessor processor in Processors)
        {
            processor.Diagnostics(builder);
        }
    }

    /// <summary>
    /// Get the pipeline state of the firmware stage from a given channel
    /// </summary>
    /// <param name="channel"></param>
    public Pipelines.PipelineStackItem GetFirmwareState(CodeChannel channel) => Processors[(int)channel].FirmwareStackItem;

    /// <summary>
    /// Push a new state on the stack of a given channel procesor. Only to be used by the SPI channel processor!
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="file">Optional file</param>
    /// <returns>Pipeline state</returns>
    public Pipelines.PipelineStackItem Push(CodeChannel channel, CodeFile? file = null) => Processors[(int)channel].Push(file);

    /// <summary>
    /// Push a new state on the stack of a given pipeline. Only to be used by the SPI channel processor!
    /// </summary>
    /// <param name="channel">Code channel</param>
    public void Pop(CodeChannel channel) => Processors[(int)channel].Pop();

    /// <summary>
    /// Assign the job file to the given channel. Only used by the job tasks!
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="file">Job file</param>
    public void SetJobFile(CodeChannel channel, CodeFile? file) => Processors[(int)channel].SetJobFile(file);

    /// <summary>
    /// Wait for all pending codes to finish
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <param name="flushAll">Flush all codes on all stack levels</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public Task<bool> FlushAsync(CodeChannel channel, bool flushAll = false, CancellationToken cancellationToken = default) => Processors[(int)channel].FlushAsync(flushAll, cancellationToken);

    /// <summary>
    /// Wait for all pending codes of the given file to finish
    /// </summary>
    /// <param name="file">Code file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public Task<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default) => Processors[(int)file.Channel].FlushAsync(file, cancellationToken);

    /// <summary>
    /// Wait for all pending codes on the same stack level as the given code to finish.
    /// By default this replaces all expressions as well for convenient parsing by the code processors.
    /// </summary>
    /// <param name="code">Code waiting for the flush</param>
    /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
    /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
    /// <param name="syncFileStreams">Whether the file streams are supposed to be synchronized (if applicable)</param>
    /// <param name="ifExecuting">Return true only if the corresponding code input is actually active (ignored if syncFileStreams is true)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool evaluateAll = true, bool syncFileStreams = false, bool ifExecuting = true, CancellationToken cancellationToken = default)
    {
        // Wait for the pending codes on this channel to go
        if (!await Processors[(int)code.Channel].FlushAsync(code, evaluateExpressions, evaluateAll, cancellationToken))
        {
            return false;
        }

        if (syncFileStreams && code.IsFromFileChannel)
        {
            // Wait for both file streams to reach the same position
            if (await DoSyncAsync(code, cancellationToken))
            {
                await code.UpdateNextFilePositionAsync();
                return true;
            }
            return false;
        }
        else if (ifExecuting)
        {
            // Make sure the current code channel is executing G/M/T-codes
            using (await _model.AccessReadOnlyAsync(cancellationToken))
            {
                if (_model.Inputs[code.Channel]?.Active != true)
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
    public async ValueTask StartCodeAsync(Commands.Code code)
    {
        ChannelProcessor processor = Processors[(int)code.Channel];
        PipelineStage stage = PipelineStage.Start;

        // Deal with priority codes
        if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
        {
            // Process this priority code here if it is idle
            if (processor.IsIdle(code))
            {
                await processor.WriteCodeAsync(code, stage);
                return;
            }

            // Otherwise move it to another idle code channel with the same emulation type (if possible)
            using (await _model.AccessReadOnlyAsync())
            {
                Compatibility compatibility = _model.Inputs[code.Channel]?.Compatibility ?? Compatibility.RepRapFirmware;
                for (int input = 0; input < Inputs.Total; input++)
                {
                    CodeChannel channel = (CodeChannel)input;
                    if (channel != code.Channel && channel is not CodeChannel.File and not CodeChannel.File2)
                    {
                        ChannelProcessor next = Processors[input];
                        if (_model.Inputs[channel]?.Compatibility == compatibility && next.IsIdle(code))
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
                    ChannelProcessor next = Processors[input];
                    if (next.IsIdle(code))
                    {
                        code.Channel = channel;
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
    public readonly CancellationTokenSource[] CancellationTokenSources = new CancellationTokenSource[Inputs.Total];

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
            CancellationTokenSources[(int)channel] = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        }
    }

    /// <summary>
    /// Execute a given code on a given pipeline stage
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    public void CodeCompleted(Commands.Code code) => Processors[(int)code.Channel].WriteCode(code, PipelineStage.Executed);

    /// <summary>
    /// Dictionary of codes vs. synchronization tasks
    /// </summary>
    private readonly Dictionary<Code, TaskCompletionSource<bool>> _syncRequests = [];

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
    public async Task<bool> DoSyncAsync(Code code, CancellationToken cancellationToken = default)
    {
        if (!code.IsFromFileChannel)
        {
            throw new ArgumentException("Code is not from a file channel");
        }
        if (code.FilePosition is null)
        {
            throw new ArgumentException("Code has no file position and cannot be used for sync requests", nameof(code));
        }

        if (!Processors[(int)CodeChannel.File].HasValidJobFile && !Processors[(int)CodeChannel.File2].HasValidJobFile)
        {
            // There is nothing to sync if the files have finished or if there is only one file stream...
            return true;
        }

        Task<bool> syncTask;
        lock (_syncRequests)
        {
            foreach (Code item in _syncRequests.Keys)
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
            foreach (Code code in _syncRequests.Keys.ToList())
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
