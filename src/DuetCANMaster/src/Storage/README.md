# Storage

`src/Storage/` holds the checksum implementations. There is no file storage in this firmware: the files are DSF's.

## Key Files

| File | Purpose |
|---|---|
| [CRC32.cpp](CRC32.cpp) / [CRC32.h](CRC32.h) | CRC32, used for the SPI link's headers and data and to check the firmware image at startup. |
| [CRC16.cpp](CRC16.cpp) / [CRC16.h](CRC16.h) | CRC16, which nothing else in this firmware calls. |

The SBC-side implementations in DuetRealtimeCore and DuetControlServer have to produce identical values.
