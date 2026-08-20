using DuetControlServer.Commands;
using DuetControlServer.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Class representing an execution level on a given pipeline.
/// This is the effective target for incoming codes
/// </summary>
public sealed class PipelineStackItem
{
    // Private fields
    private readonly PipelineBase _pipeline;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="pipeline">Pipeline holding this stack item</param>
    /// <param name="file">Current file or null if not present</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="settings">Settings to use</param>
    /// <param name="lifetime">Host application lifetime</param>
    public PipelineStackItem(PipelineBase pipeline, CodeFile? file, CodeProcessor codeProcessor, Settings settings, IHostApplicationLifetime lifetime)
    {
        _pipeline = pipeline;
        _settings = settings;
        _lifetime = lifetime;
        _pipeline = pipeline;

        if (pipeline.Stage != PipelineStage.Executed)
        {
            PendingCodes = Channel.CreateBounded<Code>(new BoundedChannelOptions(_settings.MaxCodesPerInput)
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
        ProcessorTask = Task.Factory.StartNew(async delegate
        {
            await foreach (Code code in PendingCodes.Reader.ReadAllAsync(_lifetime.ApplicationStopping))
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
                        codeProcessor.CancelCode(code);
                    }
                    else
                    {
                        await pipeline.ProcessCodeAsync(code);
                    }
                }
                catch (Exception e)
                {
                    pipeline.ChannelProcessor.Logger.LogError(e, "Failed to process code in stage {0}", pipeline.Stage);
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
    /// Current code being executed.
    /// </summary>
    public Code? CodeBeingExecuted;

    /// <summary>
    /// Wait for the pipeline state to finish processing codes
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    /// <remarks>This method does not throw an exception even if the cancellation token is triggered</remarks>
    public async ValueTask<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            await _idleEvent.WaitAsync(cancellationToken);
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
            _pipeline.ChannelProcessor.Logger.LogError("Pipeline failed to store code immediately so waiting synchronously for it to be added");
            PendingCodes.Writer.WriteAsync(code, _lifetime.ApplicationStopping).AsTask().Wait();
        }
    }

    /// <summary>
    /// Enqueue a given code asynchrously on this pipeline state for execution
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public ValueTask WriteCodeAsync(Code code, CancellationToken cancellationToken = default)
    {
        lock (this)
        {
            Busy = true;
        }
        return PendingCodes.Writer.WriteAsync(code, cancellationToken);
    }
}
