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
/// <param name="eventLogger">Event logger</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
public sealed class MotionService(
    // EventLogger eventLogger,
    LinkInterface linkInterface,
    // Model.ObjectModel model,
    // IHostApplicationLifetime lifetime,
    ILogger<MotionService> logger,
    IOptions<Settings> settings) : BackgroundService
{
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
    /// Number of logical drives the native side indexes by. Must match maxAxesPlusExtruders
    /// </summary>
    private const int NumLogicalDrives = 32;

    /// <summary>
    /// The controller's step clock, in Hz. Must match the native stepClockRate
    /// </summary>
    private const float StepClockRate = 48000000.0f / 64.0f;

    /// <summary>
    /// Feed moves to the native motion engine
    /// </summary>
    /// <remarks>
    /// <para>
    /// Placeholder for the G-code path: until the kinematics are ported to this side there is
    /// nothing to turn into moves, so this submits a slow square wave on X and Y to exercise the
    /// whole chain - submission, lookahead, preparation, the ScheduleMove packet, and the CAN send
    /// on the controller.
    /// </para>
    /// <para>
    /// What it does <em>not</em> do is build CAN movement messages here. That was the previous
    /// stand-in, and it bypassed everything the native engine exists to do
    /// </para>
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        if (!linkInterface.Native.StartMotion(settings.Value.UseRealtimeScheduling ? settings.Value.MotionRtPriority : 0))
        {
            logger.LogWarning("Native motion engine did not start; no moves will be submitted");
            return;
        }

        // A move at a time, alternating between the two axes. The endpoints are absolute, so each
        // move is planned as a delta from where the last one left the machine
        const float stepsPerMm = 80.0f;
        const float lengthMm = 20.0f;
        const float feedRateMmPerSec = 30.0f;
        const float accelerationMmPerSecSquared = 500.0f;

        byte[] buffer = new byte[MoveParams.Length(NumLogicalDrives)];
        int[] endPoints = new int[NumLogicalDrives];
        float[] directionVector = new float[NumLogicalDrives];
        uint moveId = 1;
        int axis = 0;
        bool forwards = true;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!linkInterface.Native.CanAddMove(0))
                {
                    // The ring is full, which is the normal state when moves are being executed
                    Thread.Sleep(TimeSpan.FromMilliseconds(5));
                    continue;
                }

                Array.Clear(directionVector);
                endPoints[axis] += (int)MathF.Round((forwards ? lengthMm : -lengthMm) * stepsPerMm);
                directionVector[axis] = forwards ? 1.0f : -1.0f;

                MoveParamsHeader header = new()
                {
                    MoveId = moveId,
                    OwnedDrives = uint.MaxValue,
                    Flags = MoveFlags.CanPauseAfter | MoveFlags.XyMoving | MoveFlags.UsingStandardFeedrate,
                    TotalDistance = lengthMm,
                    // Both in the firmware's internal units: mm per step clock, and mm per step
                    // clock squared. Converting once here keeps it out of every consumer natively
                    MaxAcceleration = accelerationMmPerSecSquared / (StepClockRate * StepClockRate),
                    RequestedSpeed = feedRateMmPerSec / StepClockRate,
                    RingNumber = 0,
                    NumDrives = NumLogicalDrives
                };

                int length = MoveParams.Write(buffer, header, endPoints, directionVector);
                if (linkInterface.Native.SubmitMove(buffer.AsSpan(0, length)))
                {
                    moveId++;
                    axis ^= 1;
                    if (axis == 0)
                    {
                        forwards = !forwards;
                    }
                }
                else
                {
                    // Refused rather than dropped: undo the endpoint so the retry describes the same
                    // move. Submitting the next one from the advanced position would silently double
                    // the distance travelled
                    endPoints[axis] -= (int)MathF.Round((forwards ? lengthMm : -lengthMm) * stepsPerMm);
                    Thread.Sleep(TimeSpan.FromMilliseconds(5));
                }
            }
        }
        finally
        {
            linkInterface.Native.StopMotion();
        }
    }
}
