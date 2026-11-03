# CAN

`src/CAN/` is the main-board CAN-FD subsystem. It makes this board the bus master for Duet3Expansion boards: it carries DSF's moves and messages onto the bus, distributes the time, and reports what comes back.

## Key Files

| File | Purpose |
|---|---|
| [CanInterface.cpp](CanInterface.cpp) | CAN-FD transport, the sender and receiver tasks, and the time sync broadcast. |
| [CanMotion.cpp](CanMotion.cpp) | Turns `ScheduleMove` packets into per-board movement messages, and stops drivers when an endstop fires. |
| [CommandProcessor.cpp](CommandProcessor.cpp) | Handles received messages and forwards them to DSF. |
| [ExpansionManager.cpp](ExpansionManager.cpp) | Tracks known boards from their announcements and status reports. |

## How It Works

DSF owns the machine view. It plans each move and sends the result as `ScheduleMove` packets, which `CanMotion` fans out to the boards the move's drivers are on. Configuration reaches the boards as `SendCANMessage` packets, which this module puts on the bus without interpreting them, and each one's outcome is reported to DSF.

Received messages are forwarded to DSF, and some are also handled here:

- input changes, because a stop has to be decided here: a round trip to the SBC would let the axis overrun the endstop. A move says which input stops which driver, using the matching rules in `lib/DuetSpiInterface/include/DuetSpiProtocol/StopRules.h`, and the stopped drivers are reported to DSF, which works out the position correction;
- board announcements, which are acknowledged only once DSF has been given them, and board status reports.

The other job is time alignment. Moves only stay in step across boards because this board broadcasts the time from its step timer, which the boards use to schedule their own step output.

## Interfaces

- [../SBC/README.md](../SBC/README.md) delivers the packets from DSF and carries the replies and reports back.
- [../Movement](../Movement) provides the step clock the time sync messages carry.
- [../Platform/README.md](../Platform/README.md) spins `ExpansionManager` in the main task.
- **Duet3Expansion**: this module is the master-side peer of its CAN handling. The on-wire contract is in `lib/CANlib`.

## Related Docs

- [CAN messages](../../../Documentation/articles/can-messages.md)
- [Firmware link](../../../Documentation/articles/firmware-link.md)
