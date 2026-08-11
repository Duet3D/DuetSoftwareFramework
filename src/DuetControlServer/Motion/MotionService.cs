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
internal sealed class MotionService(
    // EventLogger eventLogger,
    LinkInterface linkInterface,
    MovePlanner planner,
    Model.ObjectModel model,
    // IHostApplicationLifetime lifetime,
    ILogger<MotionService> logger,
    IOptions<Settings> settings) : BackgroundService
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
    /// Start a thread that performs the communication with the firmware
    /// </summary>
    /// <remarks>
    /// This effectively starts a thread with higher priority in order to ensure
    /// that the communication with the controller is not blocked by other tasks
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread wrapper = new(() =>
        {
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
                Execute(stoppingToken);
                tcs.SetResult();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    if (ae.InnerException is OperationCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            tcs.SetResult();
                        }
                        else
                        {
                            tcs.SetCanceled();
                        }
                    }
                    else
                    {
                        tcs.SetException(ae.InnerException!);
                    }
                }
                else if (e is OperationCanceledException)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        tcs.SetResult();
                    }
                    else
                    {
                        tcs.SetCanceled();
                    }
                }
                else
                {
                    tcs.SetException(e);
                }
            }
        })
        {
            Name = "DuetControlServer MotionService",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        wrapper.Start();
        return tcs.Task;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        // Shut down this service.
        await base.StopAsync(stoppingToken);
    }

    /// <summary>
    /// Run the motion engine
    /// </summary>
    /// <remarks>
    /// <para>
    /// This thread no longer produces moves - the G-code path does that now, through
    /// <see cref="MovePlanner"/>. What is left is the engine's lifecycle: configure it before
    /// starting it, keep it running, and stop it on shutdown.
    /// </para>
    /// <para>
    /// Configuration has to come first. The engine's defaults are all zero, and a zeroed description
    /// is not a conservative one: with no steps per mm and no extruders every axis is misclassified
    /// and no move can be scheduled. Init also builds the rings from the configured depth and grace
    /// period, so a description pushed down later would not be reflected in them
    /// </para>
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        if (!planner.ReconfigureAsync(stoppingToken, adoptGeometryFromObjectModel: true).AsTask().GetAwaiter().GetResult())
        {
            logger.LogError("Could not configure the native motion engine; no moves will be executed");
            return;
        }

        if (!linkInterface.Native.StartMotion(settings.Value.UseRealtimeScheduling ? settings.Value.MotionRtPriority : 0))
        {
            logger.LogWarning("Native motion engine did not start; no moves will be executed");
            return;
        }

        logger.LogInformation("Motion engine started for {NumAxes} axes and {NumExtruders} extruders",
                              planner.Parameters.NumAxes, planner.Parameters.NumExtruders);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // The engine runs its own thread natively, so nothing here drives the motion. What
                // this loop does drive is the reported position: the object model's machinePosition
                // is the live one, which means it has to be read back from the engine rather than
                // written by whoever queued the last move
                PublishLivePosition(stoppingToken);
                Thread.Sleep(LivePositionInterval);
            }
        }
        finally
        {
            linkInterface.Native.StopMotion();
        }
    }

    /// <summary>
    /// Report where the machine actually is
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
    private void PublishLivePosition(CancellationToken cancellationToken)
    {
        MotionParameters parameters;
        int numAxes;
        using (planner.Lock())
        {
            if (linkInterface.Native.GetMotorPositions(_liveEndPoints, out _) <= 0)
            {
                return;
            }
            parameters = planner.Parameters;
            numAxes = parameters.NumAxes;
            parameters.Geometry.MotorStepsToCartesian(
                _liveEndPoints, parameters.StepsPerMm, numAxes, numAxes, _livePosition);
        }

        using (model.AccessReadWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
        {
            int count = Math.Min(numAxes, model.Move.Axes.Count);
            for (int axis = 0; axis < count; axis++)
            {
                model.Move.Axes[axis].MachinePosition = _livePosition[axis];
            }
        }
    }
}
