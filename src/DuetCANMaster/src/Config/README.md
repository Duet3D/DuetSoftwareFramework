# Config

`src/Config/` is the build-time board configuration layer. It tells the rest of the firmware which board it is being compiled for, which features are available, and how the board's pins are described.

## Key Files

| File | Purpose |
|---|---|
| [Configuration.h](Configuration.h) | Shared configuration definitions and compile-time glue. |
| [Pins.h](Pins.h) / [Pins.cpp](Pins.cpp) | Selects the board's header and holds the common pin-description code. |
| [Pins_Duet3_MB6HC.h](Pins_Duet3_MB6HC.h), [Pins_Duet3_MB6XD.h](Pins_Duet3_MB6XD.h) | Per-board pin tables and feature flags. |

## How It Works

The board's preset sets its macro through `Boards.cmake`, and that selects one of the `Pins_*` headers. Those define the processor, the available peripherals, and feature macros such as `HAS_SBC_INTERFACE` and `SUPPORT_CAN_EXPANSION`, so board variation stays out of the rest of the firmware.

## Interfaces

- [../Platform/README.md](../Platform/README.md) and [../Hardware/README.md](../Hardware/README.md) are the primary consumers.
- [../SBC/README.md](../SBC/README.md) and [../CAN/README.md](../CAN/README.md) are enabled by the feature flags defined here.
