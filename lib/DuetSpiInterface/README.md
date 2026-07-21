# DuetSpiInterface

The SPI wire protocol shared by the two ends of the Duet SBC link. This is the single source of
truth for the layout of everything that crosses the link — change it here and both sides move
together.

```
include/DuetSpiProtocol/MessageFormats.h   transfer/packet headers, request indices, constants
```

Header-only, and deliberately free of any firmware, OS or CANlib dependency, so the same text
compiles for bare-metal ARM and for 64-bit Linux.

Checksums are **not** here. CRC16 and CRC32 are standard algorithms rather than negotiated formats,
and each side brings its own implementation tuned for its environment: the firmware uses
`DuetCANMaster/src/Storage/CRC32.cpp` (slicing-by-4 on SAME70, DMAC hardware CRC on SAME5x), the SBC
side `DuetSbcInterface/sbc/src/Crc.cpp`, and DCS `Utility/{CRC16,CRC32}.cs`. All three must produce
identical values.

## Consumers

**`src/DuetSbcInterface`** (and, through its C ABI, DuetControlServer) links the `duet_spi_protocol`
INTERFACE target, which just adds the include directory:

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
