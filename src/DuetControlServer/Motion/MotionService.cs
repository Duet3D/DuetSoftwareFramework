using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Native;
using DuetControlServer.Link;
using DuetControlServer.Motion.Native;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Motion;

/// <summary>
/// This class accesses RepRapFirmware via SPI and deals with general communication
/// </summary>
/// <param name="linkInterface">Link interface</param>
/// <param name="planner">Where G-codes become queued moves</param>
/// <param name="model">Object model</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
/// <param name="timeProvider">Clock the live position is republished on</param>
internal sealed class MotionService(
    // EventLogger eventLogger,
    LinkInterface linkInterface,
    MovePlanner planner,
    MotionTracker tracker,
    Model.ObjectModel model,
    // IHostApplicationLifetime lifetime,
    ILogger<MotionService> logger,
    IOptions<Settings> settings,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>
    /// How often the live machine position is republished
    /// </summary>
    private static readonly TimeSpan LivePositionInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Scratch buffers for the live position, so publishing does not allocate</summary>
    private readonly int[] _liveEndPoints = new int[MotionLimits.MaxAxesPlusExtruders];
    private readonly float[] _livePosition = new float[MotionLimits.MaxAxesPlusExtruders];

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Run this service
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Start the motion engine, then keep the reported position up to date until shutdown
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await StartEngineAsync(stoppingToken))
        {
            return;
        }

        try
        {
            await PublishMotionAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
        finally
        {
            linkInterface.Native.StopMotion();
        }
    }

    /// <summary>
    /// Configure the motion engine and start it
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configuration has to come first. The engine's defaults are all zero, and a zeroed description
    /// is not a conservative one: with no steps per mm and no extruders every axis is misclassified
    /// and no move can be scheduled. Init also builds the rings from the configured depth and grace
    /// period, so a description pushed down later would not be reflected in them
    /// </para>
    /// <para>
    /// Nothing here produces moves - the G-code path does that, through <see cref="MovePlanner"/>.
    /// What is left is the engine's lifecycle: configure it before starting it, keep it running, and
    /// stop it on shutdown.
    /// </para>
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <returns>Whether the engine is running</returns>
    private async Task<bool> StartEngineAsync(CancellationToken stoppingToken)
    {
        // On the thread pool, where a bench running the SBC in lockstep sees the work
        if (!await planner.ReconfigureAsync(stoppingToken, adoptGeometryFromObjectModel: true))
        {
            logger.LogError("Could not configure the native motion engine; no moves will be executed");
            planner.ReportEngineFailed();
            return false;
        }

        if (!await StartNativeMotionAsync())
        {
            logger.LogError("Native motion engine did not start; no moves will be executed");
            planner.ReportEngineFailed();
            return false;
        }

        logger.LogInformation("Motion engine started for {NumAxes} axes and {NumExtruders} extruders",
                              planner.Parameters.NumAxes, planner.Parameters.NumExtruders);
        return true;
    }

    /// <summary>
    /// Start the native engine from a thread of its own placed where the motion thread is to run: the
    /// engine's native thread inherits the placement of the thread that starts it
    /// </summary>
    /// <returns>Whether it started</returns>
    private Task<bool> StartNativeMotionAsync()
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread starter = new(() =>
        {
            // Not a pool thread, so it says when it works
            using NativeActivity.RunningScope running = NativeActivity.Running();
            try
            {
                if (settings.Value.IsolateMotionThread && DuetSharedLibrary.ProcessHelpers.IsRaspberryPi())
                {
                    // Use a dedicated motion core if configured, otherwise share the interface core
                    int motionCore = settings.Value.MotionCoreId >= 0 ? settings.Value.MotionCoreId : settings.Value.IsolatedCoreId;
                    if (DuetSharedLibrary.ProcessHelpers.PinCurrentThreadToCore(motionCore))
                    {
                        logger.LogInformation("Motion thread pinned to CPU core {CoreId}", motionCore);
                    }
                    else
                    {
                        logger.LogWarning("Failed to pin Motion thread to CPU core {CoreId}", motionCore);
                    }

                    if (settings.Value.UseRealtimeScheduling)
                    {
                        // Keep motion below the interface priority so an SPI transfer is never starved by
                        // motion computation when the two share a core
                        if (DuetSharedLibrary.ProcessHelpers.SetCurrentThreadRealtimePriority(settings.Value.MotionRtPriority))
                        {
                            logger.LogInformation("Motion thread set to SCHED_FIFO priority {Priority}", settings.Value.MotionRtPriority);
                        }
                        else
                        {
                            logger.LogWarning("Failed to set Motion thread to real-time priority (needs CAP_SYS_NICE)");
                        }
                    }
                }
                tcs.SetResult(linkInterface.Native.StartMotion(settings.Value.UseRealtimeScheduling ? settings.Value.MotionRtPriority : 0));
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        })
        {
            Name = "DuetControlServer MotionService",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        starter.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Republish the live position every <see cref="LivePositionInterval"/>, and as soon as the motion ends
    /// </summary>
    /// <remarks>
    /// The engine runs its own thread natively, so nothing here drives the motion. What this does
    /// drive is the reported position: the object model's machinePosition is the live one, which
    /// means it has to be read back from the engine rather than written by whoever queued the last move
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    private async Task PublishMotionAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PublishMotionOnceAsync(stoppingToken);

            // A move ending while others follow it is not the end of the motion, which is reported at once
            using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task due = Task.Delay(LivePositionInterval, timeProvider, waitCts.Token);
            while (await Task.WhenAny(tracker.MoveEnded.WaitAsync(waitCts.Token), due) != due && planner.IsMoving)
            {
            }
            // Withdraws the wait that lost, so that it does not take the next move's end
            await waitCts.CancelAsync();
            stoppingToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Report where the machine actually is, and whether it is moving
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// <c>move.axes[].machinePosition</c> is the position of the move being executed, which is
    /// several moves behind whatever the G-code interpreter has queued. That lag is the whole point
    /// of a look-ahead, and it is why nothing may plan a move from this number -
    /// <see cref="MovementState"/> is what a move is measured from.
    /// </para>
    /// <para>
    /// The motor positions are the engine's own snapshot, so they are converted back through the
    /// kinematics rather than tracked separately: two ideas of where the machine is could disagree,
    /// and one of them would be wrong
    /// </para>
    /// </remarks>
    private async Task PublishMotionOnceAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // Read before the position, so that the position is the final one when the machine has stopped. Moves are queued
            // under this lock
            model.IsMoving = planner.IsMoving;

            int numAxes;
            using (planner.Lock())
            {
                if (linkInterface.Native.GetLivePositions(_liveEndPoints, out _) <= 0)
                {
                    return;
                }
                MotionParameters parameters = planner.Parameters;
                numAxes = parameters.NumAxes;
                parameters.Geometry.MotorStepsToCartesian(
                    _liveEndPoints, parameters.StepsPerMm, numAxes, numAxes, _livePosition);
            }

            int count = Math.Min(numAxes, model.Move.Axes.Count);
            for (int axis = 0; axis < count; axis++)
            {
                model.Move.Axes[axis].MachinePosition = _livePosition[axis];
            }
        }
    }
}
