/*
 * MachineLimits.h
 *
 * The compile-time shape of the machine the motion engine plans for.
 *
 * These bound the fixed-size arrays inside a DDA, which is why they are compile-time at all: a DDA
 * has to be a fixed-size object, allocated once from the motion arena and never freed. The axis and
 * extruder counts the machine actually has arrive at run time in MotionConfig and are never larger
 * than these.
 *
 * The values are the firmware's, from Config/Pins_Duet3_MB6HC.h. The managed mirror is
 * DuetControlServer/Motion/Native/MotionConfig.cs, whose MotionLimits must agree with them or a
 * MotionConfig is a different length on each side.
 */

#ifndef SRC_CONFIG_MACHINELIMITS_H_
#define SRC_CONFIG_MACHINELIMITS_H_

#include <cinttypes> // the motion sources use PRIu32 etc. in their debug output
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstring>

#include <ecv_duet3d.h>
#undef array // eCv's `array` keyword collides with std::array; RRF undefines it too
#undef result
#undef value

#include <General/Bitmap.h>
#include <General/SimpleMath.h>
#include <General/StringRef.h>
#include <Math/Isqrt.h>

// ---------------------------------------------------------------------------------------------
// Feature switches
//
// Each of these marks work this side intends to do and has not finished. A switch that marked a
// decision already taken - the SBC owns no drivers, so there is nothing local to step, monitor or
// stall-detect - is not a switch, it is a fact, and is stated where it matters instead.
//
// What each disabled one is still waiting for is recorded in
// src/Documentation/articles/rrf-differences.md. Anything whose disabled path does build is compiled
// by CI with the switch flipped, so that it stays that way.
// ---------------------------------------------------------------------------------------------

#define SUPPORT_ASYNC_MOVES 1		  // two DDA rings, drives owned per movement system
#define SUPPORT_NONLINEAR_EXTRUSION 1 // M592 extrusion correction

#define SUPPORT_S_CURVE 0 // 3rd-order planning: needs DDA_3rdOrder and MovementProfile
#define SUPPORT_LASER 0	  // laser power scaling: needs a PWM field on the move

// In the firmware this is __attribute__((optimize("O2"))), to force optimisation of the step ISR in
// a debug build. There is no step ISR here, and mixing per-function optimize attributes with the
// -O0 debug presets produces inlining warnings, so it expands to nothing.
#define SPEED_CRITICAL

// Branch hints. The firmware gets these from CoreIO; they mean the same thing anywhere.
#ifndef likely
#  define likely(x) __builtin_expect(!!(x), 1)
#endif

// ---------------------------------------------------------------------------------------------
// Numeric types
// ---------------------------------------------------------------------------------------------

// Microstep counts including fractional microsteps. Must agree with the firmware's setting: the
// two sides exchange the derived speeds and distances over SPI as explicit 32-bit floats, but the
// intermediate arithmetic has to round the same way for the tracked position to match the boards'.
using motioncalc_t = float;

inline motioncalc_t Msquare(motioncalc_t a) noexcept
{
	return a * a;
}

// ---------------------------------------------------------------------------------------------
// Machine limits
// ---------------------------------------------------------------------------------------------

constexpr size_t maxAxes = 30;				// maximum number of movement axes
constexpr size_t maxExtruders = 20;			// maximum number of extruders
constexpr size_t maxAxesPlusExtruders = 32; // may be <= maxAxes + maxExtruders
constexpr size_t maxDriversPerAxis = 8;

constexpr size_t xyzAxes = 3;
constexpr size_t xAxis = 0, yAxis = 1, zAxis = 2;

static_assert(maxAxesPlusExtruders <= maxAxes + maxExtruders);

// An axis's logical drive number is its axis number; an extruder's counts down from the top. This
// is how the firmware packs both into maxAxesPlusExtruders slots, and the endpoint and direction
// vectors sent from DuetControlServer are indexed the same way.
inline size_t ExtruderToLogicalDrive(size_t extruder) noexcept
{
	return maxAxesPlusExtruders - 1 - extruder;
}

inline size_t LogicalDriveToExtruder(size_t drive) noexcept
{
	return maxAxesPlusExtruders - 1 - drive;
}

using AxesBitmap = Bitmap<uint32_t>;
using ExtrudersBitmap = Bitmap<uint32_t>;
using LogicalDrivesBitmap = Bitmap<uint32_t>;

static_assert(maxAxesPlusExtruders <= AxesBitmap::MaxBits());
static_assert(maxAxesPlusExtruders <= LogicalDrivesBitmap::MaxBits());
static_assert(maxExtruders <= ExtrudersBitmap::MaxBits());

// ---------------------------------------------------------------------------------------------
// Driver identifiers
//
// Reduced from the firmware's CAN-expansion DriverId. There is no local CAN address here, so a
// driver is always remote and IsLocal() is always false - the SBC never drives a motor itself.
// ---------------------------------------------------------------------------------------------

using CanAddress = uint8_t;
constexpr CanAddress noCanAddress = 0xFF;

struct DriverId
{
	uint8_t localDriver = 0; // driver number on the board named by boardAddress
	CanAddress boardAddress = noCanAddress;

	constexpr DriverId() noexcept = default;
	constexpr DriverId(CanAddress addr, uint8_t drv) noexcept
		: localDriver(drv)
		, boardAddress(addr)
	{
	}

	[[nodiscard]] constexpr CanAddress GetBoardAddress() const noexcept { return boardAddress; }
	[[nodiscard]] static constexpr bool IsLocal() noexcept { return false; }
	[[nodiscard]] constexpr bool IsRemote() const noexcept { return boardAddress != noCanAddress; }

	constexpr bool operator<(const DriverId other) const noexcept
	{
		return boardAddress < other.boardAddress ||
			   (boardAddress == other.boardAddress && localDriver < other.localDriver);
	}

	constexpr bool operator==(const DriverId other) const noexcept
	{
		return boardAddress == other.boardAddress && localDriver == other.localDriver;
	}

	constexpr bool operator!=(const DriverId other) const noexcept { return !(*this == other); }
};

#endif /* SRC_CONFIG_MACHINELIMITS_H_ */
