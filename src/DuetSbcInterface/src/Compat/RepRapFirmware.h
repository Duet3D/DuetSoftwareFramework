/*
 * RepRapFirmware.h - compatibility shim
 *
 * The motion sources under src/Movement/ are imported from RepRapFirmware and will be re-synced
 * against it, so they keep their original #include lines and this directory supplies what those
 * lines resolve to. src/Compat is on duet_motion's include path ahead of nothing else, so
 * `#include <RepRapFirmware.h>` lands here rather than on the firmware's 900-line original.
 *
 * What this file provides is only what the code kept here actually reaches for: the feature
 * switches, the fixed-size limits, the step-clock time base, and the small vocabulary types
 * (bitmaps, DriverId, motioncalc_t). Everything genuinely portable - Bitmap, StringRef, SimpleMath,
 * the eCv annotations - comes from RRFLibraries, which is built for this host as RRFLibraries_HOST.
 *
 * Values that differ from the firmware's are called out where they appear. Values that must match
 * it - stepClockRate above all, since move start times are exchanged with the boards in those
 * ticks - are copied verbatim from src/DuetCANMaster/src/RepRapFirmware.h.
 */

#ifndef SRC_COMPAT_REPRAPFIRMWARE_H_
#define SRC_COMPAT_REPRAPFIRMWARE_H_

#include <cinttypes>		// the imported code uses PRIu32 etc. in its debug output
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstring>

#include <ecv_duet3d.h>
#undef array				// eCv's `array` keyword collides with std::array; RRF undefines it too
#undef result
#undef value

#include <General/Bitmap.h>
#include <General/SimpleMath.h>
#include <General/StringRef.h>
#include <Math/Isqrt.h>

// ---------------------------------------------------------------------------------------------
// Feature switches
//
// These are the compile-time configuration of the imported code. Unlike the firmware, where they
// vary per board, here they describe one fixed target: an SBC that plans moves and owns no drivers.
// ---------------------------------------------------------------------------------------------

#define SUPPORT_CAN_EXPANSION		1	// every drive is remote; there is no local driver at all
#define SUPPORT_ASYNC_MOVES			1	// two DDA rings, drives owned per movement system

#define SUPPORT_S_CURVE				0	// 3rd-order planning is not ported (DDA_3rdOrder, MovementProfile)
#define SUPPORT_LASER				0
#define SUPPORT_IOBITS				0
#define SUPPORT_SCANNING_PROBES		0
#define SUPPORT_PHASE_STEPPING		0
#define SUPPORT_CLOSED_LOOP			0
#define SUPPORT_REMOTE_COMMANDS		0
#define SUPPORT_NONLINEAR_EXTRUSION	0
#define SUPPORT_COORDINATE_ROTATION	0

#define HAS_SMART_DRIVERS			0
#define HAS_STALL_DETECT			0
#define HAS_VOLTAGE_MONITOR			0
#define HAS_SBC_INTERFACE			1

// In the firmware this is __attribute__((optimize("O2"))), to force optimisation of the step ISR in
// a debug build. There is no step ISR here, and mixing per-function optimize attributes with the
// -O0 debug presets produces inlining warnings, so it expands to nothing.
#define SPEED_CRITICAL

// ---------------------------------------------------------------------------------------------
// Numeric types
// ---------------------------------------------------------------------------------------------

// Microstep counts including fractional microsteps. Must agree with the firmware's setting: the
// two sides exchange the derived speeds and distances over SPI as explicit 32-bit floats, but the
// intermediate arithmetic has to round the same way for the tracked position to match the boards'.
#define USE_DOUBLE_MOTIONCALC	(0)

#if USE_DOUBLE_MOTIONCALC
using motioncalc_t = double;
#else
using motioncalc_t = float;
#endif

inline motioncalc_t Msquare(motioncalc_t a) noexcept
{
	return a * a;
}

using FilePosition = uint32_t;
constexpr FilePosition noFilePosition = 0xFFFFFFFFu;

using MovementSystemNumber = unsigned int;

// ---------------------------------------------------------------------------------------------
// Machine limits
//
// Copied from Config/Pins_Duet3_MB6HC.h. They are compile-time here for the same reason as in the
// firmware - they size the arrays inside DDA, so a DDA is a fixed-size object - even though the
// actual axis and extruder counts arrive at run time in MotionConfig.
// ---------------------------------------------------------------------------------------------

constexpr size_t maxAxes = 30;					// maximum number of movement axes
constexpr size_t maxExtruders = 20;				// maximum number of extruders
constexpr size_t maxAxesPlusExtruders = 32;		// may be <= maxAxes + maxExtruders
constexpr size_t maxDriversPerAxis = 8;

constexpr size_t xyzAxes = 3;
constexpr size_t xAxis = 0, yAxis = 1, zAxis = 2;
constexpr size_t noAxis = 0x3F;

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

constexpr AxesBitmap xyzAxesBitmap = AxesBitmap::MakeLowestNBits(xyzAxes);

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
	uint8_t localDriver = 0;			// driver number on the board named by boardAddress
	CanAddress boardAddress = noCanAddress;

	constexpr DriverId() noexcept = default;
	constexpr DriverId(CanAddress addr, uint8_t drv) noexcept : localDriver(drv), boardAddress(addr) { }

	[[nodiscard]] constexpr CanAddress GetBoardAddress() const noexcept { return boardAddress; }
	[[nodiscard]] static constexpr bool IsLocal() noexcept { return false; }
	[[nodiscard]] constexpr bool IsRemote() const noexcept { return boardAddress != noCanAddress; }

	constexpr bool operator<(const DriverId other) const noexcept
	{
		return boardAddress < other.boardAddress
			|| (boardAddress == other.boardAddress && localDriver < other.localDriver);
	}

	constexpr bool operator==(const DriverId other) const noexcept
	{
		return boardAddress == other.boardAddress && localDriver == other.localDriver;
	}

	constexpr bool operator!=(const DriverId other) const noexcept { return !(*this == other); }
};

// ---------------------------------------------------------------------------------------------
// The step clock
//
// This is the one quantity that must be identical on both sides. Move start times travel to the
// boards as absolute counts in these ticks, so a mismatch does not degrade motion, it schedules it
// at the wrong moment. See Movement/StepTimer.h for how the SBC's estimate of this clock is kept
// aligned with the controller's.
// ---------------------------------------------------------------------------------------------

constexpr uint32_t stepClockRate = 48000000 / 64;		// 750kHz, common to all Duet 3 boards
constexpr uint64_t stepClockRateSquared = (uint64_t)stepClockRate * stepClockRate;
constexpr float stepClocksToMillis = 1000.0f / (float)stepClockRate;
constexpr float stepClocksToSeconds = 1.0f / (float)stepClockRate;

constexpr unsigned int iMinutesToSeconds = 60;

static constexpr uint32_t MillisToStepClocks(uint32_t numMillis) noexcept
{
	static_assert(stepClockRate % 1000 == 0);
	return numMillis * (stepClockRate / 1000);
}

// Rounds up without std::ceil, which is not usable in a constant expression under clang.
static consteval uint32_t MicrosecondsToStepClocks(float us) noexcept
{
	const double clocks = stepClockRate * 0.000001 * us;
	const auto truncated = (uint32_t)clocks;
	return truncated + (((double)truncated < clocks) ? 1u : 0u);
}

static constexpr float ConvertSpeedFromMmPerSec(float speed) noexcept
{
	return speed * (1.0f / (float)stepClockRate);
}

static constexpr float ConvertSpeedFromMmPerMin(float speed) noexcept
{
	return speed * (1.0f / (float)(stepClockRate * iMinutesToSeconds));
}

static constexpr float InverseConvertSpeedToMmPerSec(float speed) noexcept
{
	return speed * (float)stepClockRate;
}

static constexpr float InverseConvertSpeedToMmPerMin(float speed) noexcept
{
	return speed * (float)(stepClockRate * iMinutesToSeconds);
}

static constexpr float ConvertAcceleration(float accel) noexcept
{
	return accel * (1.0f / (float)stepClockRateSquared);
}

static constexpr float InverseConvertAcceleration(float accel) noexcept
{
	return accel * (float)stepClockRateSquared;
}

// ---------------------------------------------------------------------------------------------
// Assorted helpers the imported code expects to be global
// ---------------------------------------------------------------------------------------------

// The firmware's versions exploit known alignment on Cortex-M. Here they are memcpy, which the
// compiler lowers to the same thing.
inline void Memcpyf(float *dst, const float *src, size_t numFloats) noexcept
{
	std::memcpy(dst, src, numFloats * sizeof(float));
}

inline void Memcpyu32(uint32_t *dst, const uint32_t *src, size_t numWords) noexcept
{
	std::memcpy(dst, src, numWords * sizeof(uint32_t));
}

// Branch hints. The firmware gets these from CoreIO; they mean the same thing anywhere.
#ifndef likely
# define likely(x)		__builtin_expect(!!(x), 1)
# define unlikely(x)	__builtin_expect(!!(x), 0)
#endif

// Interrupt masking, as used by the MoveSegment freelist and by RRFLibraries' Isqrt.
//
// No-ops, for the same reason as the RTOSIface types: there is no interrupt to mask. What they
// guarded is a plain data race here, and it is avoided by ownership rather than by locking - every
// MoveSegment allocation, release and traversal happens on the motion thread. Nothing may call into
// the segment machinery from another thread; the C API reads a snapshot the motion thread publishes.
using irqflags_t = uint32_t;
inline irqflags_t IrqSave() noexcept { return 0; }
inline void IrqRestore(irqflags_t /*unused*/) noexcept { }

// Milliseconds since start, for the ring's grace-period bookkeeping.
uint32_t Millis() noexcept;

// Debug output. Routed to the log sink rather than stdout: the motion thread runs SCHED_FIFO, and
// a write() to a pipe nobody is draining would block it. See Compat/Debug.cpp.
void DebugPrintf(const char *fmt, ...) noexcept __attribute__((format(printf, 1, 2)));

// Debug topic selection. The firmware reads these from M111; here nothing sets them, so every
// `if (reprap.Debug(Module::Move))` branch is compiled against a constant false.
enum class Module : uint8_t
{
	Move = 0,
	DDA,
	Num
};

// ---------------------------------------------------------------------------------------------
// Forward declarations, matching the firmware's
// ---------------------------------------------------------------------------------------------

class DDA;
class DDARing;
class MoveSegment;
class Tool;
class Platform;

#endif /* SRC_COMPAT_REPRAPFIRMWARE_H_ */
