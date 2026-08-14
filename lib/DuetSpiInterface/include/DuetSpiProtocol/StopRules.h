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

#include <cstddef>
#include <cstdint>
#include <span>

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

// What a trigger stops, which is RepRapFirmware's EndstopHitAction. It travels per driver rather
// than per move because it belongs to the endstop that fired: one move may home an axis whose
// endstop has to stop everything and an axis whose endstop stops only itself.
enum class StopAction : uint8_t
{
    none = 0,    // this driver watches nothing, so nothing it could match stops anything
    driver = 1,  // stop only the motor that triggered, while its group has others still running
    group = 2,   // stop every driver of the group - RRF's stopAxis
    all = 3      // stop every driver of the move - RRF's stopAll
};

// Value of stopGroup meaning "this driver belongs to no group", so `group` stops it alone.
inline constexpr uint8_t kNoStopGroup = 0xFF;

// One driver of the move in flight, and the input that stops it.
//
// This is what the move said when it was scheduled: ScheduleMoveDriver's stop fields, kept beside
// the driver they belong to. Matching an incoming change against it needs no lookup and no knowledge
// of what an endstop means, which is the whole reason the controller can do the stopping - a round
// trip to the SBC would let the axis overrun.
struct DriverStopWatch
{
    uint8_t driverBoard;    // CAN address of the board carrying the driver this would stop
    uint8_t driverNumber;   // its number on that board
    uint8_t inputBoard;     // CAN address of the board carrying the input it watches
    uint16_t inputHandle;   // RemoteInputHandle of that input
    uint8_t stopGroup;      // drivers stopped together, or kNoStopGroup
    StopAction stopAction;  // what a trigger matching this watch stops
    bool stillRunning;      // false once this move has already stopped this driver
};

// What one trigger came to: which watch it matched, and what that watch stops.
struct StopDecision
{
    StopAction action;  // none if the trigger stops nothing
    size_t matched;     // index of the watch it matched, meaningless if action is none
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

// How many drivers of a group have not yet been stopped by this move.
[[nodiscard]] constexpr unsigned int RunningInGroup(std::span<const DriverStopWatch> watches,
                                                    uint8_t stopGroup) noexcept
{
    unsigned int running = 0;
    for (const DriverStopWatch& watch : watches)
    {
        if (watch.stopGroup == stopGroup && watch.stillRunning)
        {
            ++running;
        }
    }
    return running;
}

// What an incoming trigger comes to: the first watch it matches, and what that watch stops.
//
// `driver` escalates to `group` once it is the last motor of its group still running. That is
// RepRapFirmware's Acknowledge, which drops a stopped driver from the monitored set and lets
// CheckTriggered fall through to stopAxis when numDriversLeft reaches one. Without it, a gantry
// squaring itself on individual stalls would stop its last motor and leave the axis mid-move with
// nothing to end it.
[[nodiscard]] constexpr StopDecision DecideStop(std::span<const DriverStopWatch> watches,
                                                uint8_t inputBoard, uint16_t inputHandle,
                                                uint32_t reading) noexcept
{
    for (size_t i = 0; i < watches.size(); ++i)
    {
        const DriverStopWatch& watch = watches[i];
        if (!watch.stillRunning || !WatchMatches(watch, inputBoard, inputHandle, reading))
        {
            continue;
        }
        if (watch.stopAction == StopAction::driver && RunningInGroup(watches, watch.stopGroup) <= 1)
        {
            return { StopAction::group, i };
        }
        return { watch.stopAction, i };
    }
    return { StopAction::none, 0 };
}

// Whether the driver at `index` is one of the drivers that decision stops.
[[nodiscard]] constexpr bool StopsDriver(std::span<const DriverStopWatch> watches,
                                         const StopDecision& decision, size_t index) noexcept
{
    if (index >= watches.size() || decision.matched >= watches.size())
    {
        return false;
    }
    switch (decision.action)
    {
    case StopAction::all:
        return true;
    case StopAction::group:
        return watches[index].stopGroup != kNoStopGroup
               && watches[index].stopGroup == watches[decision.matched].stopGroup;
    case StopAction::driver:
        return index == decision.matched;
    case StopAction::none:
    default:
        return false;
    }
}

}  // namespace duet::spi::protocol

#endif  // DUETSPIPROTOCOL_STOPRULES_H
