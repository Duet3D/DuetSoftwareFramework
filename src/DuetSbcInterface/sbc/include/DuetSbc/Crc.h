// CRC helpers for the SBC side of the SPI link.
//
// These are standard algorithms, so they are not part of the shared wire-protocol library: the
// firmware computes the same values with its own tuned implementation (DuetCANMaster's
// Storage/CRC32.cpp, which uses slicing-by-4 on SAME70 and the DMAC hardware CRC unit on SAME5x),
// and DCS with Utility/{CRC16,CRC32}.cs. All three must agree bit-for-bit or transfers are rejected,
// so do not "optimise" these into a different parameterisation.
#pragma once

#include <cstddef>
#include <cstdint>

namespace duet::sbc {

// CRC16-IBM/ARC, reflected, init 0xFFFF, no final XOR (matches Utility/CRC16.cs).
// Used for protocol versions < 4.
uint16_t Crc16(const uint8_t *buffer, size_t length) noexcept;

// CRC32 (zlib / IEEE 802.3), reflected, poly 0xEDB88320, init 0xFFFFFFFF, final XOR 0xFFFFFFFF
// (matches Utility/CRC32.cs). Used for protocol versions >= 4.
uint32_t Crc32(const uint8_t *buffer, size_t length) noexcept;

} // namespace duet::sbc
