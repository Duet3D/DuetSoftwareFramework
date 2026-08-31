using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Files.Job;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Events;

/// <summary>
/// Deals with events, one at a time, by running the macro named after each one
/// </summary>
/// <remarks>
/// <para>
/// The port of <c>GCodes::ProcessEvent</c>. RepRapFirmware polls for events from its AutoPause G-code
/// buffer when that buffer is otherwise idle, because it has one thread and a state machine; this
/// awaits instead, on the same channel. Running on <see cref="CodeChannel.Autopause"/> is what keeps
/// an event macro from consuming the job or trigger channels, which is why RepRapFirmware uses that
/// buffer for it too.
/// </para>
/// <para>
/// The macro is the whole response when it exists. Only when it does not does the default action run,
/// which is what lets a machine replace the behaviour rather than add to it
/// </para>
/// </remarks>
/// <param name="queue">Events waiting to be dealt with</param>
/// <param name="macroRunner">Macro runner</param>
/// <param name="eventLogger">Event logger</param>
/// <param name="logger">Logger</param>
internal sealed class EventProcessor(EventQueue queue, MacroRunner macroRunner, EventLogger eventLogger,
                                   Files.Job.JobController jobController, ILogger<EventProcessor> logger) : BackgroundService
{
    /// <summary>
    /// Why a job would pause because of an event, or null if the event does not pause one
    /// </summary>
    /// <param name="type">Event type</param>
    /// <returns>The pause reason, or null</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Event::GetDefaultPauseReason</c>, and deliberately the same three events.
    /// Which events pause is its decision to make and this is only reading it back - an event it does
    /// not pause for still does not pause here. What differs is <em>how</em> the machine stops; see
    /// JOB_LIFECYCLE.md §3.5.1
    /// </remarks>
    private static PrintPausedReason? DefaultPauseReason(EventType type) => type switch
    {
        EventType.HeaterFault => PrintPausedReason.HeaterFault,
        EventType.FilamentError => PrintPausedReason.Filament,
        EventType.DriverError => PrintPausedReason.DriverError,
        _ => null
    };

    /// <summary>
    /// What to do for an event whose macro is absent and whose default action is not just to say so
    /// </summary>
    /// <remarks>
    /// Only <c>controller_reconnect</c> has one: a controller that has come back has to be configured
    /// again, and that recovery cannot live in a macro a machine may not have. Set by the link, which
    /// is what knows how to run the startup files
    /// </remarks>
    public Func<CancellationToken, Task>? ReconnectDefaultAction { get; set; }
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.WaitForEventAsync(stoppingToken);
                if (!queue.TryStartProcessing(out MachineEvent? machineEvent))
                {
                    continue;
                }

                try
                {
                    await ProcessAsync(machineEvent!, stoppingToken);
                }
                finally
                {
                    // However the macro ended, including having thrown or been cancelled. An event left
                    // at the head of the queue would stop every later event from ever being dealt with
                    queue.FinishedProcessing();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The next event is not the failed one's fault, and this service stopping would mean
                // no event is ever dealt with again
                logger.LogError(e, "Failed to process an event");
            }
        }
    }

    /// <summary>
    /// Deal with one event
    /// </summary>
    /// <param name="machineEvent">Event to deal with</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task ProcessAsync(MachineEvent machineEvent, CancellationToken cancellationToken)
    {
        Message description = EventText.Describe(machineEvent);
        string macroName = EventText.GetMacroFileName(machineEvent.Type);
        logger.LogDebug("Processing event {Type} from board {Board}: {Text}", machineEvent.Type, machineEvent.BoardAddress, description.Content);

        // What the macro can read about the event it was started for. B is passed even though every
        // board here is a CAN board, so that one macro works on a Duet without expansion boards too
        Dictionary<string, object?> parameters = new()
        {
            ["D"] = (int)machineEvent.DeviceNumber,
            ["B"] = (int)machineEvent.BoardAddress,
            ["P"] = (int)machineEvent.Param,
            ["S"] = description.Content
        };

        if (await macroRunner.TryRunAsync(CodeChannel.Autopause, macroName, parameters: parameters, cancellationToken: cancellationToken))
        {
            return;
        }

        // No macro, so the default action
        // TODO: raise the message box RepRapFirmware raises alongside the pause, titled "Printing
        // paused" while printing and "Event notification" otherwise, once M291 exists
        eventLogger.LogOutput(description);

        if (machineEvent.Type == EventType.ControllerReconnect && ReconnectDefaultAction is not null)
        {
            // The machine came back and nothing said what that should mean, so put it back as it was
            await ReconnectDefaultAction(cancellationToken);
        }

        if (DefaultPauseReason(machineEvent.Type) is PrintPausedReason reason)
        {
            await PauseForEventAsync(machineEvent.Type, reason, cancellationToken);
        }
    }

    /// <summary>
    /// Pause the job because an event asked for it and no macro handled it
    /// </summary>
    /// <param name="type">Event type</param>
    /// <param name="reason">Why the job is pausing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// <para>
    /// The pause is a feedhold: nobody typed it, and the reasons an event pauses - a heater that has
    /// faulted, filament that has run out, a driver reporting an error - are all cases where running
    /// the rest of the queue prints air or damages the part. JOB_LIFECYCLE.md §3.5.1 is the decision
    /// and what it does not change.
    /// </para>
    /// <para>
    /// A driver error runs no macro. RepRapFirmware routes it to <c>eventPausing2</c> rather than
    /// <c>eventPausing1</c> because <c>pause.g</c> typically lifts and parks the head, and a driver
    /// in error cannot be trusted to move. The feedhold is still right for it - it asks that driver
    /// for strictly less motion than draining the queue would
    /// </para>
    /// </remarks>
    private async Task PauseForEventAsync(EventType type, PrintPausedReason reason, CancellationToken cancellationToken)
    {
        // Whether a job that is already stopping is paused a second time is the transition table's
        // to decide, which is RepRapFirmware's test in the processingEvent state made in one place
        PauseMacro macro = type == EventType.DriverError ? PauseMacro.None : PauseMacro.Pause;
        Message result = await jobController.PauseAsync(new PauseRequest(CodeChannel.Autopause, reason, macro,
                                                                         Synchronous: false, ReportPosition: false),
                                                        cancellationToken);
        if (result.Type == MessageType.Error)
        {
            logger.LogWarning("Could not pause the job for event {Type}: {Message}", type, result.Content);
        }
    }
}
