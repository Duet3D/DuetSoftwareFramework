using DuetAPI;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes;

/// <summary>
/// Class delegating parallel G/M/T-code execution for a single code channel.
/// Every instance holds the code pipeline elements through which incoming G/M/T-codes are sent.
/// Note that code files and events disrupting the code flow require their own stack level to maintain the correct order of code execution.
/// </summary>
public sealed class ChannelProcessor 
{
    /// <summary>
    /// Channel of this pipeline
    /// </summary>
    public readonly CodeChannel Channel;

    /// <summary>
    /// Logger instance
    /// </summary>
    public readonly ILogger<ChannelProcessor> Logger;

    /// <summary>
    /// Pipelines for code flow
    /// </summary>
    private readonly Lazy<Pipelines.PipelineBase[]> _pipelines;

    /// <summary>
    /// Constructor for the channel processor
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="serviceProvider">Service provider to create pipeline instances</param>
    public ChannelProcessor(CodeChannel channel, ILogger<ChannelProcessor> logger, IServiceProvider serviceProvider)
    {
        Channel = channel;
        Logger = logger;
        _ownQueueNumber = (channel == CodeChannel.File2) ? 1 : 0;
        _commandedQueues.Push((channel == CodeChannel.Queue2) ? 1 : 0);

        _pipelines = new Lazy<Pipelines.PipelineBase[]>(() => [
            ActivatorUtilities.CreateInstance<Pipelines.Start>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Pre>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.ProcessInternally>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Post>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Firmware>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Executed>(serviceProvider, this)
        ]);
    }

    /// <summary>
    /// Commanded motion system per stack level, mirroring RRF's per-machine-state commandedQueueNumber (M596).
    /// Levels are pushed and popped together with the pipeline stack so an M596 inside a macro ends with it
    /// </summary>
    private readonly Stack<int> _commandedQueues = new();

    /// <summary>
    /// Fixed motion system of this channel, mirroring RRF's ownQueueNumber
    /// </summary>
    private readonly int _ownQueueNumber;

    /// <summary>
    /// Mirrors RRF's executeAllCommands flag, cleared only on the file channels while the input reader is forked
    /// </summary>
    public bool ExecuteAllCommands { get; set; } = true;

    /// <summary>
    /// Whether this channel is currently executing G/M/T-codes, mirroring RRF's GCodeMachineState.Executing().
    /// Kept locally because the object model copy of inputs[].active lags behind executed M596 codes
    /// </summary>
    public bool IsExecuting
    {
        get
        {
            lock (_commandedQueues)
            {
                return ExecuteAllCommands || _commandedQueues.Peek() == _ownQueueNumber;
            }
        }
    }

    /// <summary>
    /// Update the commanded motion system of the current stack level after M596 was executed on this channel
    /// </summary>
    /// <param name="queueNumber">New commanded motion system number</param>
    public void SetCommandedQueue(int queueNumber)
    {
        lock (_commandedQueues)
        {
            _commandedQueues.Pop();
            _commandedQueues.Push(queueNumber);
        }
    }

    /// <summary>
    /// Copy the commanded motion systems from another channel when the file input reader is forked
    /// </summary>
    /// <param name="other">Channel processor to copy from</param>
    public void CopyCommandedQueuesFrom(ChannelProcessor other)
    {
        lock (_commandedQueues)
        {
            lock (other._commandedQueues)
            {
                _commandedQueues.Clear();
                foreach (int queueNumber in other._commandedQueues.Reverse())
                {
                    _commandedQueues.Push(queueNumber);
                }
            }
        }
    }

    /// <summary>
    /// Pipeline stages that support push/pop
    /// </summary>
    private readonly PipelineStage[] StagesWithStack = [.. Enum.GetValues<PipelineStage>().Where(value => value != PipelineStage.Executed)];

    /// <summary>
    /// Retrieve the firmware state
    /// </summary>
    internal Pipelines.PipelineStackItem FirmwareStackItem => _pipelines.Value[(int)PipelineStage.Firmware].CurrentStackItem;

    /// <summary>
    /// Lifecycle of this pipeline
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public Task ExecuteAsync() => Task.WhenAll(_pipelines.Value.Select(stage => stage.WaitForCompletionAsync()));

    /// <summary>
    /// Get diagnostics from this pipeline
    /// </summary>
    /// <param name="builder">String builder to write to</param>
    public void Diagnostics(StringBuilder builder)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            if (pipeline.Stage != PipelineStage.Firmware)
            {
                pipeline.Diagnostics(builder);
            }
        }
    }

    /// <summary>
    /// Push a new state on the stack
    /// </summary>
    /// <returns>New pipeline state of the firmware for the SPI connector</returns>
    public Pipelines.PipelineStackItem Push(CodeFile? file)
    {
        lock (_commandedQueues)
        {
            _commandedQueues.Push(_commandedQueues.Peek());
        }

        Pipelines.PipelineStackItem? newState = null;
        foreach (PipelineStage stage in StagesWithStack)
        {
            if (stage == PipelineStage.Firmware)
            {
                newState = _pipelines.Value[(int)stage].Push(file);
            }
            else
            {
                _pipelines.Value[(int)stage].Push(file);
            }
        }
        return newState!;
    }

    /// <summary>
    /// Pop the last state from the stack
    /// </summary>
    public void Pop()
    {
        lock (_commandedQueues)
        {
            if (_commandedQueues.Count > 1)
            {
                _commandedQueues.Pop();
            }
        }

        foreach (PipelineStage stage in StagesWithStack)
        {
            _pipelines.Value[(int)stage].Pop();
        }
    }

    /// <summary>
    /// Set the job file of this channel
    /// </summary>
    /// <param name="file">Job file</param>
    public void SetJobFile(CodeFile? file)
    {
        foreach (PipelineStage stage in StagesWithStack)
        {
            _pipelines.Value[(int)stage].SetJobFile(file);
        }
    }

    /// <summary>
    /// Check if the pipeline has a valid job file assigned
    /// </summary>
    public bool HasValidJobFile
    {
        get => _pipelines.Value[0].HasValidJobFile;
    }

    /// <summary>
    /// Check if all stages starting with a certain one are idle
    /// </summary>
    /// <param name="code">Optional code requesting the check</param>
    /// <returns>True if the pipeline is empty</returns>
    public bool IsIdle(Commands.Code? code = null)
    {
        foreach (PipelineStage stage in StagesWithStack)
        {
            if (!_pipelines.Value[(int)stage].IsIdle(code))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Wait for all pending codes to finish
    /// </summary>
    /// <param name="flushAll">Whether to flush all states</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async ValueTask<bool> FlushAsync(bool flushAll, CancellationToken cancellationToken = default)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            //Logger.LogDebug("Flushing codes on stage {Stage}", pipeline.Stage);
            if (!await pipeline.FlushAsync(flushAll, cancellationToken))
            {
                Logger.LogDebug("Failed to flush codes on stage {Stage}", pipeline.Stage);
                return false;
            }
            //Logger.LogDebug("Flushed codes on stage {Stage}", pipeline.Stage);
        }
        return true;
    }

    /// <summary>
    /// Wait for all pending codes on the same stack level as the given file to finish
    /// </summary>
    /// <param name="file">Code file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async ValueTask<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            //Logger.LogDebug("Flushing file codes on stage {Stage} for {Code}", pipeline.Stage, code);
            if (!await pipeline.FlushAsync(file, cancellationToken))
            {
                Logger.LogDebug("Failed to flush file codes on stage {Stage} for {File}", pipeline.Stage, file.FilePath.Virtual);
                return false;
            }
            //Logger.LogDebug("Flushed file codes on stage {Stage} for {Code}", pipeline.Stage, code);
        }
        return true;
    }

    /// <summary>
    /// Wait for all pending codes on the same stack level as the given code to finish.
    /// By default this replaces all expressions as well for convenient parsing by the code processors.
    /// </summary>
    /// <param name="code">Code waiting for the flush</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async ValueTask<bool> FlushAsync(Commands.Code code, CancellationToken cancellationToken = default)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            if (code.Stage == PipelineStage.Executed || pipeline.Stage > code.Stage)
            {
                //Logger.LogDebug("Flushing codes on stage {Stage} for {Code}", pipeline.Stage, code);
                if (!await pipeline.FlushAsync(code, cancellationToken))
                {
                    Logger.LogDebug("Failed to flush codes on stage {Stage} for {Code}", pipeline.Stage, code);
                    return false;
                }
                //Logger.LogDebug("Flushed codes on stage {Stage} for {Code}", pipeline.Stage, code);
            }
        }
        return true;
    }

    /// <summary>
    /// Execute a given code on this pipeline stage.
    /// This should not be used unless the corresponding code channel is unbounded
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    /// <param name="stage">Stage level to enqueue it at</param>
    public void WriteCode(Commands.Code code, PipelineStage stage)
    {
        //Logger.LogDebug("Sending code {Code} to stage {Stage}", code, stage);
        _pipelines.Value[(int)stage].WriteCode(code);
        //Logger.LogDebug("Sent code {Code} to stage {Stage}", code, stage);
    }

    /// <summary>
    /// Execute a given code on a given pipeline stage
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    /// <param name="stage">Stage level to enqueue it at</param>
    public async ValueTask WriteCodeAsync(Commands.Code code, PipelineStage stage)
    {
        //Logger.LogDebug("Sending code {Code} to stage {Stage}", code, stage);
        await _pipelines.Value[(int)stage].WriteCodeAsync(code);
        //Logger.LogDebug("Sent code {Code} to stage {Stage}", code, stage);
    }
}
