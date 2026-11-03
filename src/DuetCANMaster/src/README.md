# DuetCANMaster Source Modules

DuetCANMaster is a single firmware image, so the top-level directories of `src/` are its documentation boundary.

| Module | Focus |
|---|---|
| [SBC](SBC/README.md) | SPI/USB link to DSF: transfers, packet dispatch, and the reports sent back. |
| [CAN](CAN/README.md) | CAN-FD bus master: moves, forwarded messages, board tracking, and the endstop stop path. |
| [Movement](Movement) | The step timer every board's clock is synchronised to, and the move timing constants. |
| [Platform](Platform/README.md) | Main task, cooperative housekeeping, task setup, diagnostics, and output buffers. |
| [Hardware](Hardware/README.md) | SAME70 startup, I/O ports, non-volatile memory, resets, and fault handling. |
| [Config](Config/README.md) | Build-time board configuration and feature flags. |
| [Storage](Storage/README.md) | The CRC16 and CRC32 implementations. |

`libc` and `libcpp` are imported support code rather than first-party modules.

The shared wire formats are not here: the SPI protocol is in `lib/DuetSpiInterface` and the CAN messages are in `lib/CANlib`.
