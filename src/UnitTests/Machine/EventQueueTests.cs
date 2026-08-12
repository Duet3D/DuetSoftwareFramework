using DuetAPI.ObjectModel;
using DuetControlServer.Events;
using System;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTests.Machine
{
    /// <summary>
    /// The three rules that make the event queue a queue of events rather than a list of messages:
    /// priority order, a head that stays put while it is being dealt with, and one entry per occurrence
    /// </summary>
    [TestFixture]
    public class EventQueueTests
    {
        private EventQueue _queue;

        [SetUp]
        public void SetUp() => _queue = new EventQueue(NullLogger<EventQueue>.Instance);

        private static MachineEvent Event(EventType type, byte device = 0, byte board = 1, ushort param = 0, string text = "") =>
            new(type, param, board, device, text);

        [Test]
        public void MostUrgentComesFirst()
        {
            _queue.Raise(Event(EventType.Undervoltage));
            _queue.Raise(Event(EventType.DriverError, device: 1));
            _queue.Raise(Event(EventType.DriverStall, device: 2));

            Assert.That(_queue.TryStartProcessing(out MachineEvent first), Is.True);
            Assert.That(first!.Type, Is.EqualTo(EventType.DriverError));
            _queue.FinishedProcessing();

            Assert.That(_queue.TryStartProcessing(out MachineEvent second), Is.True);
            Assert.That(second!.Type, Is.EqualTo(EventType.DriverStall));
        }

        [Test]
        public void PriorityIsNotTheWireValue()
        {
            // controller_disconnect is 128 on the wire and first in the queue
            _queue.Raise(Event(EventType.DriverError, device: 1));
            _queue.Raise(Event(EventType.ControllerDisconnect, board: 0));

            Assert.That(_queue.TryStartProcessing(out MachineEvent first), Is.True);
            Assert.That(first!.Type, Is.EqualTo(EventType.ControllerDisconnect));
        }

        [Test]
        public void EqualPrioritiesKeepTheirOrder()
        {
            _queue.Raise(Event(EventType.DriverError, device: 1));
            _queue.Raise(Event(EventType.DriverError, device: 2));

            Assert.That(_queue.TryStartProcessing(out MachineEvent first), Is.True);
            Assert.That(first!.DeviceNumber, Is.EqualTo(1));
            _queue.FinishedProcessing();

            Assert.That(_queue.TryStartProcessing(out MachineEvent second), Is.True);
            Assert.That(second!.DeviceNumber, Is.EqualTo(2));
        }

        [Test]
        public void TheHeadStaysPutWhileItIsDealtWith()
        {
            _queue.Raise(Event(EventType.DriverStall, device: 1));
            Assert.That(_queue.TryStartProcessing(out MachineEvent running), Is.True);

            // Something more urgent arrives mid-macro and waits its turn rather than displacing it
            _queue.Raise(Event(EventType.DriverError, device: 2));
            Assert.That(_queue.TryStartProcessing(out MachineEvent stillRunning), Is.True);
            Assert.That(stillRunning, Is.EqualTo(running));

            _queue.FinishedProcessing();
            Assert.That(_queue.TryStartProcessing(out MachineEvent next), Is.True);
            Assert.That(next!.Type, Is.EqualTo(EventType.DriverError));
        }

        [Test]
        public void TheSameOccurrenceIsQueuedOnce()
        {
            Assert.That(_queue.Raise(Event(EventType.DriverError, device: 1, param: 4, text: "at 40C")), Is.True);

            // Same type, device, board and parameter: one fault reporting itself twice, whatever it says
            Assert.That(_queue.Raise(Event(EventType.DriverError, device: 1, param: 4, text: "at 45C")), Is.False);
            Assert.That(_queue.Count, Is.EqualTo(1));

            // A different device, board or parameter is a different occurrence
            Assert.That(_queue.Raise(Event(EventType.DriverError, device: 2, param: 4)), Is.True);
            Assert.That(_queue.Raise(Event(EventType.DriverError, device: 1, board: 2, param: 4)), Is.True);
            Assert.That(_queue.Raise(Event(EventType.DriverError, device: 1, param: 5)), Is.True);
            Assert.That(_queue.Count, Is.EqualTo(4));
        }

        [Test]
        public void SuppressionCoversTheOneBeingDealtWith()
        {
            _queue.Raise(Event(EventType.HeaterFault, device: 1));
            Assert.That(_queue.TryStartProcessing(out _), Is.True);

            // Still the same fault, and its macro is still running
            Assert.That(_queue.Raise(Event(EventType.HeaterFault, device: 1)), Is.False);
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void CountersSayWhatHasHappened()
        {
            _queue.Raise(Event(EventType.DriverWarning, device: 1));
            _queue.Raise(Event(EventType.DriverWarning, device: 2));
            Assert.That(_queue.Queued, Is.EqualTo(2));
            Assert.That(_queue.Processed, Is.EqualTo(0));

            _queue.TryStartProcessing(out _);
            _queue.FinishedProcessing();
            Assert.That(_queue.Processed, Is.EqualTo(1));

            // A suppressed event is not a queued one
            _queue.Raise(Event(EventType.DriverWarning, device: 2));
            Assert.That(_queue.Queued, Is.EqualTo(2));
        }

        [Test]
        public void FinishingWithoutStartingDoesNothing()
        {
            _queue.Raise(Event(EventType.DriverWarning, device: 1));
            _queue.FinishedProcessing();

            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(_queue.Processed, Is.EqualTo(0));
        }

        [Test]
        public void TheLeastUrgentIsDroppedWhenFull()
        {
            for (int i = 0; i < EventQueue.MaxEvents; i++)
            {
                Assert.That(_queue.Raise(Event(EventType.DriverError, device: (byte)i)), Is.True);
            }
            Assert.That(_queue.Count, Is.EqualTo(EventQueue.MaxEvents));

            // An undervoltage is less urgent than every driver error already waiting, so it is what goes
            _queue.Raise(Event(EventType.Undervoltage, device: 200));
            Assert.That(_queue.Count, Is.EqualTo(EventQueue.MaxEvents));

            // ...and a more urgent one displaces the last driver error instead
            _queue.Raise(Event(EventType.ControllerDisconnect, board: 0));
            Assert.That(_queue.Count, Is.EqualTo(EventQueue.MaxEvents));
            Assert.That(_queue.TryStartProcessing(out MachineEvent first), Is.True);
            Assert.That(first!.Type, Is.EqualTo(EventType.ControllerDisconnect));
        }

        [Test]
        public void EveryEventTypeNamesItsMacro()
        {
            // A type added without a macro name would otherwise get a plausible file name nobody wrote
            foreach (EventType type in System.Enum.GetValues<EventType>())
            {
                string macro = EventText.GetMacroFileName(type);
                Assert.That(macro, Does.EndWith(".g"));
                Assert.That(macro, Does.Not.StartWith("event-"), $"{type} has no macro name");
                Assert.That(macro, Does.Not.Contain("_"), $"{type} keeps an underscore in its macro name");
            }

            Assert.That(EventText.GetMacroFileName(EventType.HeaterFault), Is.EqualTo("heater-fault.g"));
            Assert.That(EventText.GetMacroFileName(EventType.ControllerDisconnect), Is.EqualTo("controller-disconnect.g"));
            Assert.That(EventText.GetMacroFileName(EventType.Overvoltage), Is.EqualTo("overvoltage.g"));
        }

        [Test]
        public void HeaterFaultTextCoversEveryFaultType()
        {
            // CANlib asserts this of its own copy at compile time. C# cannot assert an array's length
            // there, so it is asserted here: a fault type added to the schema without a string would
            // otherwise be reported as "unknown error" by a machine that knows exactly what happened
            Assert.That(EventText.HeaterFaultText, Has.Length.EqualTo((int)HeaterFaultType.HeaterFaultTypeLimit + 1),
                        "one string per HeaterFaultType, plus one for a value that is none of them");

            // The last entry is the one a value outside the enum falls back to, so it cannot be a real
            // fault type's text
            Assert.That(EventText.HeaterFaultText[^1], Is.EqualTo("unknown error: "));

            foreach (HeaterFaultType type in System.Enum.GetValues<HeaterFaultType>())
            {
                if (type != HeaterFaultType.HeaterFaultTypeLimit)
                {
                    Assert.That(EventText.Describe(Event(EventType.HeaterFault, param: (ushort)type)).Content,
                                Does.Not.Contain("unknown error"), $"{type} has no text of its own");
                }
            }
        }

        [Test]
        public void EventsAreDescribedAsTheFirmwareDescribesThem()
        {
            Message described = EventText.Describe(Event(EventType.HeaterFault, device: 2, param: 1, text: "expected 200 measured 150"));
            Assert.That(described.Content, Is.EqualTo("Heater 2 fault: temperature rising too slowly: expected 200 measured 150"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Error));

            // A fault type CANlib does not have falls back to the last entry, as it does in RRF
            Assert.That(EventText.Describe(Event(EventType.HeaterFault, device: 0, param: 99)).Content,
                        Is.EqualTo("Heater 0 fault: unknown error: "));

            described = EventText.Describe(Event(EventType.DriverStall, board: 2, device: 3));
            Assert.That(described.Content, Is.EqualTo("Driver 2.3 stall"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Warning));

            described = EventText.Describe(Event(EventType.Undervoltage, board: 4, param: 118));
            Assert.That(described.Content, Is.EqualTo("undervoltage on board 4: voltage 11.8V"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Warning));

            described = EventText.Describe(Event(EventType.ExpansionTimeout, board: 5));
            Assert.That(described.Content, Is.EqualTo("Expansion board 5 stopped sending status"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Error));

            described = EventText.Describe(Event(EventType.ControllerDisconnect, board: 0, text: "Transfer timeout"));
            Assert.That(described.Content, Is.EqualTo("Lost connection to the controller: Transfer timeout"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Error));

            Assert.That(EventText.Describe(Event(EventType.ControllerReconnect, board: 0, param: 1)).Content,
                        Is.EqualTo("Connection to the controller re-established, it had reset"));
        }

        [Test]
        public void DriverStatusIsDescribedAsTheBoardDescribesIt()
        {
            // Bit 1 is an error, bit 0 a warning, bit 16 information; a driver error reports errors alone
            const uint overTemperatureShutdown = 1u << 1, overTemperatureWarning = 1u << 0, standstill = 1u << 16;

            Assert.That(DriverStatusText.Describe(0, DriverStatusText.Severity.All), Is.EqualTo("ok"));
            Assert.That(DriverStatusText.Describe(overTemperatureShutdown, DriverStatusText.Severity.ErrorsOnly),
                        Is.EqualTo("over temperature shutdown"));
            Assert.That(DriverStatusText.Describe(overTemperatureWarning, DriverStatusText.Severity.ErrorsOnly),
                        Is.EqualTo("ok"), "a warning is not an error");
            Assert.That(DriverStatusText.Describe(overTemperatureWarning, DriverStatusText.Severity.WarningsAndErrors),
                        Is.EqualTo("over temperature warning"));
            Assert.That(DriverStatusText.Describe(standstill, DriverStatusText.Severity.WarningsAndErrors),
                        Is.EqualTo("ok"), "information is neither");
            Assert.That(DriverStatusText.Describe(standstill, DriverStatusText.Severity.All), Is.EqualTo("standstill"));

            // More than one, in bit order whatever order they are set in
            Assert.That(DriverStatusText.Describe(overTemperatureWarning | overTemperatureShutdown, DriverStatusText.Severity.All),
                        Is.EqualTo("over temperature warning, over temperature shutdown"));

            // And that is what a driver event says
            Message described = EventText.Describe(Event(EventType.DriverError, board: 1, device: 2, param: (ushort)overTemperatureShutdown));
            Assert.That(described.Content, Is.EqualTo("Driver 1.2 error: over temperature shutdown"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Error));

            described = EventText.Describe(Event(EventType.DriverWarning, board: 1, device: 2, param: (ushort)overTemperatureWarning));
            Assert.That(described.Content, Is.EqualTo("Driver 1.2 warning: over temperature warning"));
            Assert.That(described.Type, Is.EqualTo(MessageType.Warning));
        }

        [Test]
        public void TheSeverityMasksPartitionTheMeanings()
        {
            // CANlib asserts both of these of its own copy at compile time
            Assert.That(StandardDriverStatus.ErrorMask & StandardDriverStatus.WarningMask, Is.Zero);
            Assert.That(StandardDriverStatus.ErrorMask & StandardDriverStatus.InfoMask, Is.Zero);
            Assert.That(StandardDriverStatus.InfoMask & StandardDriverStatus.WarningMask, Is.Zero);

            uint covered = StandardDriverStatus.ErrorMask | StandardDriverStatus.WarningMask | StandardDriverStatus.InfoMask;
            Assert.That(covered, Is.EqualTo((1u << StandardDriverStatus.BitMeanings.Length) - 1),
                        "every bit with a meaning has a severity, and every severity bit has a meaning");
        }

        [Test]
        public async Task WaitingReturnsWhenAnEventArrives()
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            Task waiting = _queue.WaitForEventAsync(cts.Token);
            Assert.That(waiting.IsCompleted, Is.False);

            _queue.Raise(Event(EventType.DriverError, device: 1));
            await waiting;

            // One already waiting returns at once
            await _queue.WaitForEventAsync(cts.Token);
        }
    }
}
