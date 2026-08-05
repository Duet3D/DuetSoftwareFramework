/*
 * MoveParams.h
 *
 * The move as DuetControlServer hands it down: everything DDA::InitStandardMove works out for a
 * single move on its own, and nothing that depends on the moves either side of it.
 *
 * This is the split. DCS interprets the G-code, runs the kinematics, works out where each drive
 * ends up and how fast the move is allowed to go; that is steps 1-6 of the firmware's
 * InitStandardMove. It stops there, because step 7 onwards - lookahead, melding one move into the
 * next, deciding the actual start and end speeds - needs the whole ring, and the ring lives here.
 *
 * Units are the firmware's internal ones, not the user's: speed is mm per step clock, acceleration
 * mm per step clock squared, and the endpoints are microsteps. DCS converts once, on its side,
 * rather than every consumer converting here.
 *
 * The C# mirror is DuetControlServer/Motion/Native/MoveParams.cs and must stay byte-for-byte
 * identical, exactly as for LinkEvents.h. Sizes are asserted on both sides.
 */

#ifndef SRC_MOTION_MOVEPARAMS_H_
#define SRC_MOTION_MOVEPARAMS_H_

#include <RepRapFirmware.h>

#include <span>

namespace Duet::Sbc::Motion
{
	// Bits of MoveParamsHeader::flags. These are the subset of the firmware's DDA flags that survive
	// the split: the ones native still reads during lookahead, preparation or retirement. The rest
	// (doneIoBits, hadLookaheadUnderrun, ...) are either set here or are DCS's business alone.
	namespace MoveFlags
	{
		// The move may be paused after, i.e. it is not part of an indivisible sequence
		inline constexpr uint32_t canPauseAfter = 1u << 0;
		// The move monitors endstops or a Z probe. Always an isolated move as well
		inline constexpr uint32_t checkEndstops = 1u << 1;
		// The move runs at the standard feed rate, so a feed rate change may be applied while queued
		inline constexpr uint32_t usingStandardFeedrate = 1u << 2;
		// Apply pressure advance to forward extrusion in this move
		inline constexpr uint32_t usePressureAdvance = 1u << 3;
		// Both XY movement and extrusion, i.e. the printing jerk limits apply
		inline constexpr uint32_t isPrintingMove = 1u << 4;
		// Movement along an X or Y axis was asked for, even if it rounds to no steps
		inline constexpr uint32_t xyMoving = 1u << 5;
		// An extruder-only move, or one involving reverse extrusion
		inline constexpr uint32_t isNonPrintingExtruderMove = 1u << 6;
		// Continuous rotation axes took the short way round
		inline constexpr uint32_t continuousRotationShortcut = 1u << 7;
		// Do not meld this move with its neighbours, and let it finish before starting the next
		inline constexpr uint32_t isolatedMove = 1u << 8;
		// Some extruder moves forwards during this move (M571)
		inline constexpr uint32_t hasForwardExtrusion = 1u << 9;
	}

#pragma pack(push, 1)

	// Fixed part of a move submission. Two arrays follow it in the same record:
	//
	//     int32_t endPoint[numDrives];        machine position each drive ends at, microsteps
	//     float   directionVector[numDrives]; normalised direction, first three entries Cartesian
	//
	// numDrives is the configured maxAxesPlusExtruders rather than the number of drives that
	// actually move, because MatchSpeeds, RecalculateMove and Prepare all index densely by logical
	// drive. That is 288 bytes a move; a sparse encoding would save most of it and cost indexing
	// complexity everywhere, which is not a trade worth making before anything has been measured.
	struct MoveParamsHeader
	{
		// DCS's correlation id, quoted back in MoveCompleted and MoveFailed. Never zero
		uint32_t moveId;
		// LogicalDrivesBitmap of the drives this move is allowed to touch (SUPPORT_ASYNC_MOVES)
		uint32_t ownedDrives;
		// MoveFlags
		uint32_t flags;
		// Length of the move in hypercuboid space, mm
		float totalDistance;
		// Acceleration and deceleration limit, always positive, mm/clock^2. Native may lower this
		// for an acceleration-only or deceleration-only move, so it is a limit and not a promise
		float maxAcceleration;
		// The speed asked for, mm/clock, already limited by DCS to the axis maxima and whatever
		// Kinematics::LimitSpeedAndAcceleration allows
		float requestedSpeed;
		// Which ring to queue this move on: 0 or 1 (SUPPORT_ASYNC_MOVES)
		uint8_t ringNumber;
		// Entries in each of the two trailing arrays
		uint8_t numDrives;
		uint16_t padding;
	};

#pragma pack(pop)

	static_assert(sizeof(MoveParamsHeader) == 28, "MoveParamsHeader must be 28 bytes");
	static_assert(offsetof(MoveParamsHeader, totalDistance) == 12 );
	static_assert(offsetof(MoveParamsHeader, ringNumber) == 24 );

	// Value of a stopOnInput entry meaning "this driver watches no endstop during this move".
	inline constexpr uint32_t kNoStopInput = 0xFFFFFFFF;

	// Pack the CAN address and RemoteInputHandle of an endstop into a stopOnInput entry.
	[[nodiscard]] constexpr uint32_t MakeStopInput(uint8_t boardAddress, uint16_t inputHandle) noexcept
	{
		return (static_cast<uint32_t>(boardAddress) << 16) | inputHandle;
	}

	[[nodiscard]] constexpr uint8_t StopInputBoard(uint32_t stopOnInput) noexcept
	{
		return static_cast<uint8_t>(stopOnInput >> 16);
	}

	[[nodiscard]] constexpr uint16_t StopInputHandle(uint32_t stopOnInput) noexcept
	{
		return static_cast<uint16_t>(stopOnInput);
	}

	// Which switch, if any, stops each driver of a drive during this move.
	//
	// This is RepRapFirmware's SwitchEndstop reduced to what a move needs. That class holds a board
	// number per port and derives the handle from the axis and the port index, so the board is the
	// only part that differs between one switch of an axis and the next; the handle follows from
	// which switch it is. The switches of an axis may be spread over several boards, as they may in
	// the firmware.
	//
	// numSwitches says how the drivers share them, exactly as SwitchEndstop::PrimeAxis decides it:
	//   0  this drive watches nothing during this move
	//   1  every driver of the drive watches boards[0], so the first to trigger stops the axis
	//   n  driver i watches boards[i], so each motor runs on to its own switch
	//
	// A stall endstop is the case where n switches are not n handles. A board reports every driver
	// that stalled under one board-wide handle, RemoteInputHandle(typeStallEndstop, 0, 0), so what
	// tells one driver's stall from another's is the board rather than the handle. See below.
	struct MoveStopInput
	{
		// RemoteInputHandle the switches are registered under, with a minor field of zero. For an
		// endstop handle driver i watches minor i, which is why only one handle has to be carried
		uint16_t handle;
		uint8_t numSwitches;
		uint8_t boards[maxDriversPerAxis];		// CAN address of each switch, in driver order
		uint8_t padding;						// declared so the C# mirror can match it
	};

	// The handle type field, which decides whether the minor field is per-driver. See
	// RemoteInputHandle in CANlib: minor is bits 0-5, major 6-11, type 12-15.
	inline constexpr uint16_t kHandleTypeShift = 12;
	inline constexpr uint16_t kHandleTypeEndstop = 1;		// RemoteInputHandle::typeEndstop

	static_assert(sizeof(MoveStopInput) == 4 + maxDriversPerAxis, "MoveStopInput layout");
	static_assert(offsetof(MoveStopInput, boards) == 3);

	// A drive that watches nothing, which is what every drive of an ordinary move carries.
	inline constexpr MoveStopInput kNoStopSwitches{};

	// The packed board and handle that one driver of a drive watches, or kNoStopInput.
	//
	// A driver past the end of a per-driver list watches nothing rather than falling back to the
	// first switch: it has no switch of its own, and stopping it on another motor's would defeat the
	// point of giving each motor one.
	//
	// The minor field is derived per driver only for an endstop handle. That convention comes from
	// M574, which registers switch i of an axis under minor i; nothing else numbers its inputs that
	// way. A stall endstop in particular is reported under one handle per board whatever the driver,
	// so deriving a minor for it would name a handle the board never reports and the move would run
	// on as if it had no endstop at all.
	[[nodiscard]] constexpr uint32_t StopInputForDriver(const MoveStopInput& stop, size_t driverIndex) noexcept
	{
		if (stop.numSwitches == 0)
		{
			return kNoStopInput;
		}
		if (stop.numSwitches == 1)
		{
			return MakeStopInput(stop.boards[0], stop.handle);
		}
		if (driverIndex >= stop.numSwitches || driverIndex >= maxDriversPerAxis)
		{
			return kNoStopInput;
		}

		if ((stop.handle >> kHandleTypeShift) != kHandleTypeEndstop)
		{
			return MakeStopInput(stop.boards[driverIndex], stop.handle);
		}

		constexpr uint16_t minorMask = 0x3F;			// RemoteInputHandle::minor is 6 bits wide
		const auto handle = static_cast<uint16_t>((stop.handle & ~minorMask) | (driverIndex & minorMask));
		return MakeStopInput(stop.boards[driverIndex], handle);
	}

	// Total size of a submission carrying `numDrives` drives.
	[[nodiscard]] constexpr size_t MoveParamsLength(size_t numDrives) noexcept
	{
		return sizeof(MoveParamsHeader) + (numDrives * (sizeof(int32_t) + sizeof(float) + sizeof(MoveStopInput)));
	}

	// The two trailing arrays. Both are read straight out of the record, so callers must not assume
	// alignment beyond the 4 bytes the header's size guarantees.
	//
	// These return spans rather than pointers, so numDrives is applied once here instead of at every
	// call site. That is worth doing precisely because numDrives arrives with the record: it is the
	// value that decides how far a reader walks, and a reader that re-derives the bound itself is a
	// reader that can get it wrong. The caller is still responsible for having checked that the
	// record is long enough to hold what the header claims - see MotionService::SubmitMove.
	[[nodiscard]] inline std::span<const int32_t> MoveParamsEndPoints(const MoveParamsHeader& header) noexcept
	{
		// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) - the tail is part of the record
		const auto *const first = reinterpret_cast<const int32_t *>(reinterpret_cast<const char *>(&header) + sizeof(header));
		return {first, header.numDrives};
	}

	[[nodiscard]] inline std::span<const float> MoveParamsDirectionVector(const MoveParamsHeader& header) noexcept
	{
		const std::span<const int32_t> endPoints = MoveParamsEndPoints(header);
		// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) - the tail is part of the record
		const auto *const first = reinterpret_cast<const float *>(endPoints.data() + endPoints.size());
		return {first, header.numDrives};
	}

	// Which switches, if any, stop each drive during this move. Only meaningful when the move carries
	// MoveFlags::checkEndstops; every entry has numSwitches zero otherwise.
	//
	// It is per drive rather than per move so that one move can home several axes at once, each
	// stopping on its own endstop. The entries travel all the way down to the controller, which is
	// what actually watches for the input change: it is the only place close enough to the CAN bus
	// for the axis not to overrun before the stop takes effect.
	[[nodiscard]] inline std::span<const MoveStopInput> MoveParamsStopInputs(const MoveParamsHeader& header) noexcept
	{
		const std::span<const float> directionVector = MoveParamsDirectionVector(header);
		// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) - the tail is part of the record
		const auto *const first = reinterpret_cast<const MoveStopInput *>(directionVector.data() + directionVector.size());
		return {first, header.numDrives};
	}

	// The same three, for filling a record in. numDrives must already be set: it is what says where
	// each array begins and how long each span is.
	[[nodiscard]] inline std::span<int32_t> MoveParamsEndPoints(MoveParamsHeader& header) noexcept
	{
		// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) - the tail is part of the record
		auto *const first = reinterpret_cast<int32_t *>(reinterpret_cast<char *>(&header) + sizeof(header));
		return {first, header.numDrives};
	}

	[[nodiscard]] inline std::span<float> MoveParamsDirectionVector(MoveParamsHeader& header) noexcept
	{
		const std::span<int32_t> endPoints = MoveParamsEndPoints(header);
		// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) - the tail is part of the record
		auto *const first = reinterpret_cast<float *>(endPoints.data() + endPoints.size());
		return {first, header.numDrives};
	}

	[[nodiscard]] inline std::span<MoveStopInput> MoveParamsStopInputs(MoveParamsHeader& header) noexcept
	{
		const std::span<float> directionVector = MoveParamsDirectionVector(header);
		// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) - the tail is part of the record
		auto *const first = reinterpret_cast<MoveStopInput *>(directionVector.data() + directionVector.size());
		return {first, header.numDrives};
	}
}

#endif /* SRC_MOTION_MOVEPARAMS_H_ */
