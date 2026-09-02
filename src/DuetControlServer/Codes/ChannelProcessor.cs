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
    /// Serializes every change of the stack of levels
    /// </summary>
    /// <remarks>
    /// A level is pushed and popped across every stage as one operation, and two poppers race for
    /// the same level: the macro runner that owns it pops it when the macro ends, and an unwind (a
    /// pause abandoning macros, an abort) pops it as it walks the stack down. Held across the whole
    /// check-and-pop, so exactly one of them takes each level
    /// </remarks>
    private readonly Lock _levelLock = new();

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

        _pipelines = new Lazy<Pipelines.PipelineBase[]>(() => [
            ActivatorUtilities.CreateInstance<Pipelines.Start>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Pre>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.ProcessInternally>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Post>(serviceProvider, this),
            ActivatorUtilities.CreateInstance<Pipelines.Executed>(serviceProvider, this)
        ]);
    }

    /// <summary>
    /// Pipeline stages that support push/pop
    /// </summary>
    private readonly PipelineStage[] StagesWithStack = [.. Enum.GetValues<PipelineStage>().Where(value => value != PipelineStage.Executed)];

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
            pipeline.Diagnostics(builder);
        }
    }

    /// <summary>
    /// Push a new state on the stack
    /// </summary>
    /// <param name="file">File the new state executes, if any</param>
    public void Push(CodeFile? file)
    {
        lock (_levelLock)
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                _pipelines.Value[(int)stage].Push(file);
            }
        }
    }

    /// <summary>
    /// Pop the topmost state from the stack if it is still the given file's
    /// </summary>
    /// <param name="file">File whose level is to be popped</param>
    /// <returns>True if the level was popped, false if another popper already took it</returns>
    /// <remarks>
    /// The macro runner pops the level it pushed when the macro ends, and an unwind pops levels as
    /// it walks the stack down; whichever asks second finds the file no longer on top and leaves
    /// the stack alone, so an abandoned macro's runner does not pop the level beneath its own
    /// </remarks>
    public bool PopIfCurrent(CodeFile file)
    {
        lock (_levelLock)
        {
            if (!ReferenceEquals(CurrentFile, file))
            {
                return false;
            }
            foreach (PipelineStage stage in StagesWithStack)
            {
                _pipelines.Value[(int)stage].Pop();
            }
            return true;
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
    /// How many levels this channel's stack holds, counting the base level
    /// </summary>
    public int StackDepth => _pipelines.Value[(int)PipelineStage.Start].StackDepth;

    /// <summary>
    /// File on top of this channel's stack, or null if it is not running one
    /// </summary>
    public CodeFile? CurrentFile => _pipelines.Value[(int)PipelineStage.Start].CurrentStackItem.File;

    /// <summary>
    /// Whether every macro running on this channel can be restarted from its beginning
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GCodeMachineState::CanRestartMacro</c>, which walks its stack and returns
    /// false if any level is a macro that has not said it is restartable. A pause abandons the macros
    /// it unwinds, so one that cannot be restarted must not be interrupted part-way - the pause waits
    /// until the channel is back out of it instead. <c>M98 R1</c> is what marks one restartable
    /// </remarks>
    public bool CanRestartMacros
    {
        get
        {
            foreach (CodeFile? file in _pipelines.Value[(int)PipelineStage.Start].StackedFiles())
            {
                if (file is MacroFile macro && !macro.IsPausable)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Whether the macro this channel is executing was restarted after a pause
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::GetMacroRestarted</c>: the channel is inside a macro and the
    /// level that started it is still on its first command since a restart. Published as
    /// <c>state.macroRestarted</c> for the file channel
    /// </remarks>
    public bool IsMacroRestarted
    {
        get
        {
            IReadOnlyList<CodeFile?> files = _pipelines.Value[(int)PipelineStage.Start].StackedFiles();
            return files.Count > 1 && files[0] is MacroFile && files[1]?.FirstCommandAfterRestart == true;
        }
    }

    /// <summary>
    /// Whether this channel is running any macro at all
    /// </summary>
    public bool IsDoingMacro
    {
        get
        {
            foreach (CodeFile? file in _pipelines.Value[(int)PipelineStage.Start].StackedFiles())
            {
                if (file is MacroFile)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Abandon the macros a pause interrupts, leaving the job file itself in place
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether any macro was abandoned</returns>
    /// <remarks>
    /// The macro half of RepRapFirmware's pause: the machine is stopping somewhere the macro did not
    /// expect, so whatever it had left to do is no longer meaningful and its codes are cancelled with
    /// it. Only macros are popped - the job file underneath them is what the resume will read from
    /// again, and it stays. This is deliberately not <see cref="AbortAllFilesAsync"/>, which unwinds
    /// everything regardless: that is right for an abort and wrong for a pause. The return value is
    /// RepRapFirmware's <c>pausedInMacro</c>, set there in the same loop that pops the machine
    /// states: the resume reads it to mark the job file's replayed command as a restart
    /// </remarks>
    public async Task<bool> AbandonMacrosForPauseAsync(CancellationToken cancellationToken = default)
    {
        // Deferred codes the purge did not claim are owed and fire as the machine decelerates; the
        // codes the purge did claim were cancelled before this runs. Draining them first means no
        // deferred code resolves into a level this is about to abandon
        while (_pipelines.Value[(int)PipelineStage.ProcessInternally].LastDeferredCodeTask() is Task deferredCodes)
        {
            await deferredCodes.WaitAsync(cancellationToken);
        }

        bool abandonedMacro = false;
        while (CurrentFile is MacroFile macro)
        {
            using (await macro.LockAsync(cancellationToken))
            {
                macro.Abort();
            }

            // The abort resolves the macro's finish, so its runner may pop the level between the
            // abort and this line; whichever of the two gets there first takes it
            PopIfCurrent(macro);
            abandonedMacro = true;
        }
        return abandonedMacro;
    }

    /// <summary>
    /// Completion of a deferred code on this channel, or null if none is deferred
    /// </summary>
    public Task? LastDeferredCodeTask() => _pipelines.Value[(int)PipelineStage.ProcessInternally].LastDeferredCodeTask();

    /// <summary>
    /// Cancel every deferred code on this channel whose anchor is at or past the given move id
    /// </summary>
    /// <param name="firstPurgedMoveId">Id of the earliest move a feedhold purged</param>
    public void CancelDeferredCodesAfter(uint firstPurgedMoveId)
        => _pipelines.Value[(int)PipelineStage.ProcessInternally].CancelDeferredCodesAfter(firstPurgedMoveId);

    /// <summary>
    /// Abort every file on this channel's stack, unwinding it back to the base level
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task AbortAllFilesAsync(CancellationToken cancellationToken = default)
    {
        // Everything pending is discarded, deferred codes included: the moves they were anchored to
        // either drain or no longer exist, and nothing pending is replayed
        _pipelines.Value[(int)PipelineStage.ProcessInternally].CancelAllDeferredCodes();

        while (CurrentFile is MacroFile macro)
        {
            using (await macro.LockAsync(cancellationToken))
            {
                macro.Abort();
            }

            // As in AbandonMacrosForPauseAsync: the aborted macro's runner races this loop for the
            // level, and exactly one of them pops it
            PopIfCurrent(macro);
        }
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
