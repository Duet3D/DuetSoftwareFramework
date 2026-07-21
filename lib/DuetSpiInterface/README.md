# DuetSpiInterface

The SPI wire protocol shared by the two ends of the Duet SBC link. This is the single source of
truth for the layout of everything that crosses the link — change it here and both sides move
together.

```
include/DuetSpiProtocol/MessageFormats.h   transfer/packet headers, request indices, constants
include/DuetSpiProtocol/Crc.h              CRC16 and CRC32 used by the transfer headers
src/Crc.cpp                                the CRC implementations
```

The headers deliberately have no firmware, OS or CANlib dependency, so the same text compiles for
bare-metal ARM and for 64-bit Linux.

## Consumers

**`src/DuetSbcInterface`** (and, through its C ABI, DuetControlServer) builds the `duet_spi_protocol`
CMake target:

```cmake
add_subdirectory("${DUET_LIBRARIES_DIR}/DuetSpiInterface" duet_spi_protocol)
target_link_libraries(<target> PUBLIC duet_spi_protocol)
```

**`src/DuetCANMaster`** (RepRapFirmware) consumes the headers only — it has its own Makefile-based
bare-metal build and does not build the CMake target. Its board makefiles add:

```make
-I$(LIBRARIES_DIR)/DuetSpiInterface/include
```

and `src/SBC/SbcMessageFormats.h` aliases these definitions to the firmware-side spellings
(`SbcTransferBufferSize`, `CANResponseHeader`, …) alongside the constants and structures that stay
local to the firmware, such as the USB transport framing.

## Changing the protocol

Wire structs are `#pragma pack(1)` and guarded by `static_assert`s on `sizeof`/`offsetof` that
encode the sizes declared by the C# `[StructLayout]` attributes in
`DuetControlServer/Link/Protocol/**`. A layout mistake is a compile error on both sides rather than
a corrupt transfer at runtime.

Bump `ProtocolVersion` whenever the format changes, and update the C# definitions to match — they
are not generated from these headers.
