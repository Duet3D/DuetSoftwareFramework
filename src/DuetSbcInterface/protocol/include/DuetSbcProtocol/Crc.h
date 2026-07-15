// CRC helpers matching the C# implementations in DuetControlServer/Utility/{CRC16,CRC32}.cs
// byte-for-byte, so checksums computed here interoperate with DCS and RepRapFirmware.
#pragma once

#include <cstddef>
#include <cstdint>

namespace duet::sbc::protocol {

// CRC16-IBM/ARC, reflected, init 0xFFFF, no final XOR (matches Utility/CRC16.cs).
// Used for protocol versions < 4.
uint16_t Crc16(const uint8_t *buffer, size_t length) noexcept;

// CRC32 (zlib / IEEE 802.3), reflected, poly 0xEDB88320, init 0xFFFFFFFF, final XOR 0xFFFFFFFF
// (matches Utility/CRC32.cs). Used for protocol versions >= 4.
uint32_t Crc32(const uint8_t *buffer, size_t length) noexcept;

} // namespace duet::sbc::protocol
