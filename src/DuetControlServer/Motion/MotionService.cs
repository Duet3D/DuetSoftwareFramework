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
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.CanMessages;
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
    EventLogger eventLogger,
    LinkInterface linkInterface,
    Model.ObjectModel model,
    IHostApplicationLifetime lifetime,
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
#if true
                if (DuetSharedLibrary.ProcessHelpers.IsRaspberryPi())
                {
                    if (DuetSharedLibrary.ProcessHelpers.PinCurrentThreadToCore(3))
                    {
                        logger.LogInformation("Motion thread pinned to CPU core 3");
                    }
                    else
                    {
                        logger.LogWarning("Failed to pin motion thread to CPU core 3");
                    }
                }
#endif
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
    /// Perform communication with the RepRapFirmware controller over SPI
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        byte seq = 0;
        do
        {
            CanMessageMovementLinearShaped msg = new()
            {
                WhenToExecute = 0,
                AccelerationClocks = 0,
                SteadyClocks = 1000,
                DecelerationClocks = 0,
                ExtruderDrives = 0,
                NumDrivers = 0,
                Seq = seq++,
                UsePressureAdvance = false,
                UseLateInputShaping = false
            };
            byte dstAddress = 2;
            // linkInterface.SendCanMessageAsync(dstAddress, msg);
            // linkInterface.SendCanMessageAsync(dstAddress, msg);

            // 5ms delay
            Thread.Sleep(TimeSpan.FromMilliseconds(5));
        }
        while (!stoppingToken.IsCancellationRequested);
    }
}
