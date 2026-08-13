/*
 * StopRules.h
 *
 * Which drivers an incoming input change stops.
 *
 * The rules are pure - given the move's watches and a trigger, they say which drivers should stop -
 * so they live here rather than in DuetCANMaster, where they would be compiled only for an ARM
 * target and reachable only through a firmware task. This header is the single definition: the
 * controller's own watch array is an array of DriverStopWatch, and the host-side tests build the
 * same struct and call the same functions. Neither side holds a copy.
 *
 * Two rules keep that true, and a change breaking either turns one definition back into two:
 *   - nothing from RepRapFirmware, CANlib or FreeRTOS may be included here, or it stops building on
 *     the host. That is why a watch carries two bytes rather than a DriverId, which is firmware-side;
 *   - no #if may make the firmware and the host see different code.
 *
 * What is NOT here is everything that is not a decision: holding the watch array across ScheduleMove
 * packets, the stop list mutex, choosing between stopping a driver provisionally and stopping one
 * that is already executing, and reporting what was stopped. Those stay in CanMotion.cpp.
 */

#ifndef DUETSPIPROTOCOL_STOPRULES_H
#define DUETSPIPROTOCOL_STOPRULES_H

#include <cstdint>

namespace duet::spi::protocol {

// A RemoteInputHandle as CANlib packs it: minor in bits 0-5, major in bits 6-11, type in bits 12-15.
inline constexpr uint16_t kHandleTypeShift = 12;
inline constexpr uint16_t kHandleTypeEndstop = 1;       // RemoteInputHandle::typeEndstop
inline constexpr uint16_t kHandleTypeStallEndstop = 5;  // RemoteInputHandle::typeStallEndstop

[[nodiscard]] constexpr uint16_t HandleType(uint16_t handle) noexcept
{
    return static_cast<uint16_t>(handle >> kHandleTypeShift);
}

[[nodiscard]] constexpr bool IsStallHandle(uint16_t handle) noexcept
{
    return HandleType(handle) == kHandleTypeStallEndstop;
}

// Most drivers a board can report a stall for, which is the width of the bitmap it sends.
inline constexpr uint8_t kMaxDriversPerBoard = 16;

// One driver of the move in flight, and the input that stops it.
//
// This is what the move said when it was scheduled: ScheduleMoveDriver::stopOnBoard and
// stopOnHandle, kept beside the driver they belong to. Matching an incoming change against it needs
// no lookup and no knowledge of what an endstop means, which is the whole reason the controller can
// do the stopping - a round trip to the SBC would let the axis overrun.
struct DriverStopWatch
{
    uint8_t driverBoard;   // CAN address of the board carrying the driver this would stop
    uint8_t driverNumber;  // its number on that board
    uint8_t inputBoard;    // CAN address of the board carrying the input it watches
    uint16_t inputHandle;  // RemoteInputHandle of that input
};

// Whether a trigger from `inputBoard` stops this driver.
//
// A switch identifies itself: the handle names the axis and the port, so board and handle are the
// whole test and `reading` is the pin's value, which says nothing about who watches it.
//
// A stall does not. Every board reports every driver that stalled under the one board-wide handle
// (stallEndstop, 0, 0), so the handle tells one driver's stall from another's not at all - the
// bitmap in `reading` does, one bit per driver of the reporting board. Ignoring it stops every armed
// driver on that board whichever one stalled, which on a move homing two axes at once records an
// axis as homed that never reached anything.
//
// The driver's own number indexes the bitmap because a driver can only be stopped by its own stall,
// so the watch's input board is the board carrying it (see MoveParams.h StopInputForSwitch).
[[nodiscard]] constexpr bool WatchMatches(const DriverStopWatch& watch, uint8_t inputBoard,
                                          uint16_t inputHandle, uint32_t reading) noexcept
{
    if (watch.inputBoard != inputBoard || watch.inputHandle != inputHandle)
    {
        return false;
    }
    if (!IsStallHandle(inputHandle))
    {
        return true;
    }
    return watch.driverNumber < kMaxDriversPerBoard
           && (reading & (static_cast<uint32_t>(1) << watch.driverNumber)) != 0;
}

}  // namespace duet::spi::protocol

#endif  // DUETSPIPROTOCOL_STOPRULES_H
