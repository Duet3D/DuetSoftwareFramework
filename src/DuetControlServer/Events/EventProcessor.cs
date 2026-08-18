using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
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
public sealed class EventProcessor(EventQueue queue, MacroRunner macroRunner, EventLogger eventLogger, ILogger<EventProcessor> logger) : BackgroundService
{
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

        // No macro, so the default action. RepRapFirmware also raises a message box and pauses the
        // print for a heater fault, a filament error or a driver error
        // TODO: raise the message box and pause once M291 and the feedhold are implemented; see
        // §3.5 of docs/devel/EVENTS_MIGRATION.md for which events pause and which of them run
        // pause.g, and §3.5.1 of docs/devel/JOB_LIFECYCLE.md for why an event pauses by feedhold
        eventLogger.LogOutput(description);

        if (machineEvent.Type == EventType.ControllerReconnect && ReconnectDefaultAction is not null)
        {
            // The machine came back and nothing said what that should mean, so put it back as it was
            await ReconnectDefaultAction(cancellationToken);
        }
    }
}
