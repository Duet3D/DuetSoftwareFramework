# Hardware

`src/Hardware/` contains the low-level support below `Platform`: startup, device definitions, the linker layout, I/O ports, non-volatile memory, resets, and fault handling.

## Key Files And Areas

| Area | Purpose |
|---|---|
| [ExceptionHandlers.cpp](ExceptionHandlers.cpp) | Fault and exception handling. |
| [IoPorts.cpp](IoPorts.cpp), [NonVolatileMemory.cpp](NonVolatileMemory.cpp), [SoftwareReset.cpp](SoftwareReset.cpp) | Shared low-level hardware helpers. |
| [SAME70/](SAME70) | Startup code, device wiring, and the linker script. |

## Interfaces

- [../Platform/README.md](../Platform/README.md) is the main consumer.
- [../Config/README.md](../Config/README.md) defines the pins and capabilities this code works with.
