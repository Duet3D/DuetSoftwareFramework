# Platform

`src/Platform/` is the runtime layer above the low-level hardware support. It holds the main task, the housekeeping that runs in it, task setup, diagnostics, and the output buffers messages are built in.

## Key Files

| File | Purpose |
|---|---|
| [RepRap.cpp](RepRap.cpp) | Owns the other classes, runs `RepRap::Spin()`, and handles emergency stops and diagnostics. |
| [Platform.cpp](Platform.cpp) | Board services: housekeeping, MCU temperature and diagnostics. |
| [Tasks.cpp](Tasks.cpp) | Firmware integrity check, RTOS start-up, and the main task. |
| [TaskPriorities.h](TaskPriorities.h) | The priorities of every task. |
| [OutputMemory.cpp](OutputMemory.cpp) | Buffers for long messages. |

## How It Works

The main task calls `RepRap::Spin()`, which spins `Platform` and `ExpansionManager` and yields. The time-critical work is not done there: the SBC link and the CAN interface have their own higher-priority tasks.

## Interfaces

- [../Config/README.md](../Config/README.md) supplies the board's capabilities and pins.
- [../Hardware/README.md](../Hardware/README.md) supplies startup and low-level support.
- [../SBC/README.md](../SBC/README.md) carries messages to DSF.
- [../CAN/README.md](../CAN/README.md) is told to stop the expansion boards on an emergency stop.
