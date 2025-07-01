using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using System;
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
/// <param name="channel">Channel to process</param>
/// <param name="serviceProvider">Service provider to use for creating pipeline stages</param>
public sealed class ChannelProcessor 
{
    /// <summary>
    /// Pipeline stages that support push/pop
    /// </summary>
    private readonly PipelineStage[] StagesWithStack = [.. Enum.GetValues<PipelineStage>().Where(value => value != PipelineStage.Executed)];

    /// <summary>
    /// Channel of this pipeline
    /// </summary>
    public readonly CodeChannel Channel;

    /// <summary>
    /// Logger instance
    /// </summary>
    public readonly NLog.Logger Logger;

    /// <summary>
    /// Pipelines for code flow
    /// </summary>
    private readonly Lazy<Pipelines.PipelineBase[]> _pipelines;

    /// <summary>
    /// Constructor for the channel processor
    /// </summary>
    /// <param name="channel">Code channe;</param>
    /// <param name="serviceProvider">Service provider to create pipeline instances</param>
    public ChannelProcessor(CodeChannel channel, IServiceProvider serviceProvider)
    {
        Channel = channel;
        Logger = NLog.LogManager.GetLogger(channel.ToString()!);

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
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(bool flushAll, CancellationToken cancellationToken = default)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            //Logger.Debug("Flushing codes on stage {0}", pipeline.Stage);
            if (!await pipeline.FlushAsync(flushAll, cancellationToken))
            {
                Logger.Debug("Failed to flush codes on stage {0}", pipeline.Stage);
                return false;
            }
            //Logger.Debug("Flushed codes on stage {0}", pipeline.Stage);
        }
        return true;
    }

    /// <summary>
    /// Wait for all pending codes on the same stack level as the given file to finish
    /// </summary>
    /// <param name="file">Code file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            //Logger.Debug("Flushing file codes on stage {0} for {1}", pipeline.Stage, code);
            if (!await pipeline.FlushAsync(file, cancellationToken))
            {
                Logger.Debug("Failed to flush file codes on stage {0} for {1}", pipeline.Stage, file.FileName);
                return false;
            }
            //Logger.Debug("Flushed file codes on stage {0} for {1}", pipeline.Stage, code);
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
    public async Task<bool> FlushAsync(Commands.Code code, CancellationToken cancellationToken = default)
    {
        foreach (Pipelines.PipelineBase pipeline in _pipelines.Value)
        {
            if (code.Stage == PipelineStage.Executed || pipeline.Stage > code.Stage)
            {
                //Logger.Debug("Flushing codes on stage {0} for {1}", pipeline.Stage, code);
                if (!await pipeline.FlushAsync(code, cancellationToken))
                {
                    Logger.Debug("Failed to flush codes on stage {0} for {1}", pipeline.Stage, code);
                    return false;
                }
                //Logger.Debug("Flushed codes on stage {0} for {1}", pipeline.Stage, code);
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
        //Logger.Debug("Sending code {0} to stage {1}", code, stage);
        _pipelines.Value[(int)stage].WriteCode(code);
        //Logger.Debug("Sent code {0} to stage {1}", code, stage);
    }

    /// <summary>
    /// Execute a given code on a given pipeline stage
    /// </summary>
    /// <param name="code">Code to enqueue</param>
    /// <param name="stage">Stage level to enqueue it at</param>
    public async ValueTask WriteCodeAsync(Commands.Code code, PipelineStage stage)
    {
        //Logger.Debug("Sending code {0} to stage {1}", code, stage);
        await _pipelines.Value[(int)stage].WriteCodeAsync(code);
        //Logger.Debug("Sent code {0} to stage {1}", code, stage);
    }
}
