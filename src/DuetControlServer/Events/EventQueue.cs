using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Events;

/// <summary>
/// Events waiting to be dealt with, in the order they should be dealt with
/// </summary>
/// <remarks>
/// <para>
/// A port of RepRapFirmware's <c>Event</c> class, whose three rules are what make an event queue
/// different from a list of messages:
/// </para>
/// <para>
/// Events are held in priority order, so a driver error is dealt with before an undervoltage warning
/// however they arrived. The one being processed stays at the head until it finishes, so the macro
/// running for it never has the queue reordered underneath it. And an event that describes an
/// occurrence already queued is dropped rather than added, so a fault reporting itself ten times a
/// second runs its macro once
/// </para>
/// </remarks>
/// <param name="logger">Logger</param>
public sealed class EventQueue(ILogger<EventQueue> logger)
{
    /// <summary>
    /// How many events may wait before the least urgent is dropped
    /// </summary>
    /// <remarks>
    /// RepRapFirmware needs no such limit: its producers are interrupt-driven and bounded by the
    /// suppression rule. M957 is not bounded by anything, and neither is a board that reports a
    /// different device number each time
    /// </remarks>
    public const int MaxEvents = 64;

    private readonly object _lock = new();
    private readonly List<QueuedEvent> _events = [];
    private readonly AsyncAutoResetEvent _eventQueued = new();

    /// <summary>
    /// One queued event and whether it is being dealt with
    /// </summary>
    private sealed class QueuedEvent(MachineEvent machineEvent)
    {
        public MachineEvent Event { get; } = machineEvent;
        public bool IsBeingProcessed { get; set; }
    }

    /// <summary>
    /// How many events have been queued since startup
    /// </summary>
    public int Queued { get; private set; }

    /// <summary>
    /// How many events have been dealt with since startup
    /// </summary>
    public int Processed { get; private set; }

    /// <summary>
    /// How many events are waiting, including one being dealt with
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>
    /// Queue an event unless the same occurrence is already queued
    /// </summary>
    /// <param name="machineEvent">Event to queue</param>
    /// <returns>True if it was queued, false if it describes something already waiting</returns>
    public bool Raise(MachineEvent machineEvent)
    {
        lock (_lock)
        {
            foreach (QueuedEvent queued in _events)
            {
                if (queued.Event.IsSameOccurrenceAs(machineEvent))
                {
                    return false;
                }
            }

            // Behind everything of the same or higher priority, and behind the head while it is being
            // dealt with, so that what is running stays where the processor expects it
            int priority = EventTypePriority.Of(machineEvent.Type);
            int index = _events.Count;
            for (int i = 0; i < _events.Count; i++)
            {
                if (!_events[i].IsBeingProcessed && EventTypePriority.Of(_events[i].Event.Type) > priority)
                {
                    index = i;
                    break;
                }
            }
            _events.Insert(index, new QueuedEvent(machineEvent));
            Queued++;

            if (_events.Count > MaxEvents)
            {
                // Say what was dropped: a queue that silently truncates reads as one that kept up
                MachineEvent dropped = _events[^1].Event;
                _events.RemoveAt(_events.Count - 1);
                logger.LogWarning("Dropped event {Type} from board {Board} device {Device}: more than {Max} events are waiting",
                                  dropped.Type, dropped.BoardAddress, dropped.DeviceNumber, MaxEvents);
            }
        }

        _eventQueued.Set();
        return true;
    }

    /// <summary>
    /// Take the most urgent event and mark it as being dealt with
    /// </summary>
    /// <param name="machineEvent">Event to deal with</param>
    /// <returns>True if there was one</returns>
    /// <remarks>
    /// It stays in the queue while it is being dealt with, which is what keeps further reports of the
    /// same occurrence from queueing behind it
    /// </remarks>
    public bool TryStartProcessing(out MachineEvent? machineEvent)
    {
        lock (_lock)
        {
            if (_events.Count == 0)
            {
                machineEvent = null;
                return false;
            }

            _events[0].IsBeingProcessed = true;
            machineEvent = _events[0].Event;
            return true;
        }
    }

    /// <summary>
    /// Drop the event that was being dealt with
    /// </summary>
    /// <remarks>
    /// Called however the processing ended, including when the macro threw or was cancelled: an event
    /// left at the head would stop every later event from ever being dealt with
    /// </remarks>
    public void FinishedProcessing()
    {
        lock (_lock)
        {
            if (_events.Count > 0 && _events[0].IsBeingProcessed)
            {
                _events.RemoveAt(0);
                Processed++;
            }
        }
    }

    /// <summary>
    /// Wait until there is an event to deal with
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task WaitForEventAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_events.Count > 0)
            {
                return;
            }
        }
        await _eventQueued.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Forget every waiting event
    /// </summary>
    /// <remarks>
    /// The counters are not reset: they say what has happened since startup, which is what makes them
    /// worth reporting
    /// </remarks>
    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}
