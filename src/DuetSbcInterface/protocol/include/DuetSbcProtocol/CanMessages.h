// CAN message bodies used by the harness.
//
// The authoritative definitions live in CANlib (lib/CANlib/src/CanMessageFormats.h), but CANlib
// cannot be included here: it targets the 32-bit embedded ILP32 ABI and its headers static_assert
// sizeof(unsigned long) == 4, which fails on 64-bit Linux (LP64). This is the same reason
// DuetControlServer reimplements the structs in C# rather than binding to CANlib. The struct below is
// therefore a 64-bit-clean mirror of CANlib's CanMessageMovementLinearShaped; keep it in sync with
// CANlib (the static_assert guards the layout).
//
// NOTE: this mirrors the *full* CANlib struct (28-byte actual length for numDrivers == 0), which is
// what the expansion board decodes -- not the truncated 20-byte C# CanMessageMovementLinearShaped.
#pragma once

#include <cstddef>
#include <cstdint>

namespace duet::sbc::protocol {

// CanMessageType values placed in the CAN id (CANlib CanId.h / Shared/CanMessageType.cs).
namespace CanMessageType {
inline constexpr uint16_t MovementLinearShaped = 52;
inline constexpr uint16_t StandardReply = 4510;
inline constexpr uint16_t UnusedMessageType = 0xFFFF;
inline constexpr uint16_t NoReply = UnusedMessageType;
} // namespace CanMessageType

// Reserved token for CAN messages that are not a reply to an outstanding request.
inline constexpr uint16_t UnsolicitedTxToken = 0xFFFF;

// CANlib Duet3Common.h
inline constexpr unsigned int MaxLinearDriversPerCanSlave = 8;

#pragma pack(push, 1)

// Mirror of CANlib's CanMessageMovementLinearShaped. The 'bits' dword packs several bitfields;
// use the setters (little-endian: the lowest declared C++ field is in the least-significant bits).
struct CanMessageMovementLinearShaped {
    uint32_t whenToExecute;
    uint32_t accelerationClocks;
    uint32_t steadyClocks;
    uint32_t decelClocks;
    // extruderDrives:8, numDrivers:4, seq:4, zero1:8, usePressureAdvance:1, useLateInputShaping:1, zero2:6
    uint32_t bits;
    float acceleration;
    float deceleration;
    union PerDriveValues {
        int32_t steps;
        float extrusion;
    } perDrive[MaxLinearDriversPerCanSlave];

    void SetExtruderDrives(uint8_t v) { bits = (bits & 0xFFFFFF00u) | static_cast<uint32_t>(v); }
    void SetNumDrivers(uint8_t v) { bits = (bits & 0xFFFFF0FFu) | ((static_cast<uint32_t>(v) & 0x0F) << 8); }
    void SetSeq(uint8_t v) { bits = (bits & 0xFFFF0FFFu) | ((static_cast<uint32_t>(v) & 0x0F) << 12); }
    void SetUsePressureAdvance(bool v) { bits = v ? (bits | 0x01000000u) : (bits & ~0x01000000u); }
    void SetUseLateInputShaping(bool v) { bits = v ? (bits | 0x02000000u) : (bits & ~0x02000000u); }

    uint8_t NumDrivers() const { return static_cast<uint8_t>((bits >> 8) & 0x0F); }

    // Number of bytes actually transmitted, matching CANlib's GetActualDataLength().
    size_t GetActualDataLength() const {
        return (sizeof(*this) - sizeof(perDrive)) + NumDrivers() * sizeof(perDrive[0]);
    }
};

#pragma pack(pop)

static_assert(sizeof(CanMessageMovementLinearShaped) == 60, "CanMessageMovementLinearShaped must be 60 bytes");

} // namespace duet::sbc::protocol
