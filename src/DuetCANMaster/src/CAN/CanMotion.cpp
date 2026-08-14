/*
 * CanMotion.cpp
 *
 *  Created on: 11 Aug 2019
 *      Author: David
 */

#include "CanMotion.h"

#if SUPPORT_CAN_EXPANSION

#  include <CanMessageBuffer.h>
#  include <CanMessageFormats.h>

#  include "CanInterface.h"
#  include <Platform/Platform.h>
#  include <Platform/RepRap.h>

#  if HAS_SBC_INTERFACE
#    include <SBC/SbcInterface.h>
#    include <SBC/SbcMessageFormats.h>
#  endif

#  include <General/FreelistManager.h>

#  include <array>

struct PrepParams
{
#  if SUPPORT_S_CURVE
	uint32_t phaseClocks[7];							// the number of step clocks for each phase
	motioncalc_t initialAcceleration, peakAcceleration; // the accelerations, always positive
	motioncalc_t initialDeceleration, peakDeceleration; // the decelerations, always negative
	motioncalc_t distances[7];							// the distances of each phase
	motioncalc_t jerk; // the magnitude of the rate of change of acceleration or deceleration, always positive; or zero
					   // if not using S-curve acceleration
#  else
	uint32_t accelClocks, steadyClocks, decelClocks;
	motioncalc_t acceleration; // the acceleration to use, always positive
	motioncalc_t deceleration; // the deceleration to use, always negative
	motioncalc_t accelDistance;
	motioncalc_t decelStartDistance;
#  endif
	motioncalc_t totalDistance;
	motioncalc_t startSpeed, topSpeed, endSpeed; // the speeds reached
#  if SUPPORT_S_CURVE
	mutable motioncalc_t phase1StartSpeed, phase1EndSpeed, phase5StartSpeed, phase5EndSpeed;
	mutable bool speedsCalculated; // true if the previous 4 speeds have been calculated and stored
#  endif

	bool useInputShaping;

#  if SUPPORT_S_CURVE
	uint32_t SteadyClocks() const noexcept { return phaseClocks[3]; }
	uint32_t TotalAccelClocks() const noexcept { return phaseClocks[0] + phaseClocks[1] + phaseClocks[2]; }
	uint32_t TotalDecelClocks() const noexcept { return phaseClocks[4] + phaseClocks[5] + phaseClocks[6]; }
	motioncalc_t TotalAccelDistance() const noexcept { return distances[0] + distances[1] + distances[2]; }
	motioncalc_t TotalDecelDistance() const noexcept { return distances[4] + distances[5] + distances[6]; }
	void EnsureSpeedsSet() const noexcept;
#  else
	[[nodiscard]] uint32_t SteadyClocks() const noexcept { return steadyClocks; }
	[[nodiscard]] uint32_t TotalAccelClocks() const noexcept { return accelClocks; }
	[[nodiscard]] uint32_t TotalDecelClocks() const noexcept { return decelClocks; }
	[[nodiscard]] motioncalc_t TotalAccelDistance() const noexcept { return accelDistance; }
#  endif

	// Get the total clocks needed
	[[nodiscard]] uint32_t TotalClocks() const noexcept { return TotalAccelClocks() + SteadyClocks() + TotalDecelClocks(); }

	void DebugPrint() const noexcept;
};

#  if SUPPORT_S_CURVE

void PrepParams::EnsureSpeedsSet() const noexcept
{
	if (!speedsCalculated)
	{
		phase1StartSpeed =
			(phaseClocks[0] == 0)
				? startSpeed
				: startSpeed + (initialAcceleration + (motioncalc_t)0.5 * jerk * (motioncalc_t)phaseClocks[0]) *
								   (motioncalc_t)phaseClocks[0];
		phase1EndSpeed = phase1StartSpeed + peakAcceleration * (motioncalc_t)phaseClocks[1];
		phase5StartSpeed = (phaseClocks[4] == 0)
							   ? topSpeed
							   : topSpeed - (motioncalc_t)0.5 * jerk * Msquare((motioncalc_t)phaseClocks[4]);
		phase5EndSpeed = phase5StartSpeed + peakDeceleration * (motioncalc_t)phaseClocks[5];
		speedsCalculated = true;
	}
}

#  endif

void PrepParams::DebugPrint() const noexcept
{
	debugPrintf("pp: td=%.3g ss=%.4g ts=%.4g es=%.4g"
#  if SUPPORT_S_CURVE
				" ad=[%.4g %.4g %.4g] sd=%.4g dd=[%.4g %.4g %.4g] a=[%.4g %.4g] d=[%.4g %.4g] ac=[%" PRIu32 " %" PRIu32
				" %" PRIu32 "] sc=%" PRIu32 " dc=[%" PRIu32 " %" PRIu32 " %" PRIu32 "]"
#  else
				" ad=%.4g dsd=%.4g a=%.4g d=%.4g ac=%" PRIu32 " sc=%" PRIu32 " dc=%" PRIu32
#  endif
				"\n",
				(double)totalDistance,
				(double)startSpeed,
				(double)topSpeed,
				(double)endSpeed,
#  if SUPPORT_S_CURVE
				(double)distances[0],
				(double)distances[1],
				(double)distances[2],
				(double)distances[3],
				(double)distances[4],
				(double)distances[5],
				(double)distances[6],
				(double)initialAcceleration,
				(double)peakAcceleration,
				(double)initialDeceleration,
				(double)peakDeceleration,
				phaseClocks[0],
				phaseClocks[1],
				phaseClocks[2],
				phaseClocks[3],
				phaseClocks[4],
				phaseClocks[5],
				phaseClocks[6]
#  else
				(double)accelDistance,
				(double)decelStartDistance,
				(double)acceleration,
				(double)deceleration,
				accelClocks,
				steadyClocks,
				decelClocks
#  endif
	);
}

namespace CanMotion
{
	enum class DriverStopState : uint8_t
	{
		Inactive = 0,
		Active,
		StopRequested,
		StopSent
	};

	// Class to record drivers active and requests to stop them
	class DriversStopList
	{
	  public:
		// NOLINTNEXTLINE(misc-unused-parameters) - parameter names come from the RRFLibraries macro
		DECLARE_FREELIST_NEW_DELETE(DriversStopList)

		DriversStopList(DriversStopList* pNext, CanAddress pBa) noexcept
			: next(pNext)
			, boardAddress(pBa)
		{
		}

		DriversStopList* next;
		CanAddress boardAddress;
		uint8_t numDrivers{};
		volatile DriverStopState stopStates[MaxLinearDriversPerCanSlave]{};
	};

	static CanMessageBuffer urgentMessageBuffer;
	static CanMessageBuffer* _ecv_null movementBufferList = nullptr;
	static DriversStopList* volatile _ecv_null stopList = nullptr;
	static uint32_t currentMoveClocks;
	static Mutex stopListMutex;
	static uint8_t nextSeq[CanId::MaxCanAddress + 1] = {0};

	static CanMessageBuffer* _ecv_null GetBuffer(const PrepParams& params, DriverId canDriver) noexcept;
	static void FreeMovementBuffers() noexcept;
} // namespace CanMotion

void CanMotion::Init() noexcept
{
	movementBufferList = nullptr;
	stopListMutex.Create("stopList");
}

void CanMotion::FreeMovementBuffers() noexcept
{
	for (;;)
	{
		CanMessageBuffer* _ecv_null p = movementBufferList;
		if (p == nullptr)
		{
			break;
		}
		movementBufferList = p->next;
		CanMessageBuffer::Free(p);
	}
}

void CanMotion::StartMovement() noexcept
{
	FreeMovementBuffers(); // there shouldn't be any movement buffers in the list, but free any that there may be

	// Free up any stop list items left over from the previous move
	const MutexLocker lock(stopListMutex);

	for (;;)
	{
		DriversStopList* _ecv_null p = stopList;
		if (p == nullptr)
		{
			break;
		}
		stopList = p->next;
		delete p;
	}
}

// If there is an existing CAN buffer for this move and CAN address, return it; otherwise create one
CanMessageBuffer* _ecv_null CanMotion::GetBuffer(const PrepParams& params, DriverId canDriver) noexcept
{
	if (canDriver.localDriver >= MaxLinearDriversPerCanSlave)
	{
		return nullptr; // can't handle a local driver number this large, the message isn't big enough
	}

	// Search for an existing buffer
	CanMessageBuffer* _ecv_null buf = movementBufferList;
	while (buf != nullptr && buf->id.Dst() != canDriver.boardAddress)
	{
		buf = buf->next;
	}

	if (buf == nullptr)
	{
		// Allocate a new movement buffer
		buf = CanMessageBuffer::Allocate();
		if (buf == nullptr)
		{
			return nullptr;
		}

		buf->next = movementBufferList;
		movementBufferList = buf;
		auto move = buf->SetupRequestMessageNoRid<CanMessageMovementLinearShaped>(
			CanInterface::GetCurrentMasterAddress(), canDriver.boardAddress);

		// Common parameters
		if (buf->next == nullptr)
		{
			// This is the first CAN-connected board for this movement
			move->accelerationClocks = params.TotalAccelClocks();
			move->steadyClocks = params.SteadyClocks();
			move->decelClocks = params.TotalDecelClocks();
			currentMoveClocks = params.TotalClocks();
		}
		else
		{
			// Save some maths by using the values from the previous buffer
			move->accelerationClocks = buf->next->msg.moveLinearShaped.accelerationClocks;
			move->steadyClocks = buf->next->msg.moveLinearShaped.steadyClocks;
			move->decelClocks = buf->next->msg.moveLinearShaped.decelClocks;
		}

#  if SUPPORT_S_CURVE
		if (params.jerk != (motioncalc_t)0.0)
		{
			// We don't support 3rd order motion on expansion boards yet, so the best we can do is compute an average
			// acceleration and scale it to unit distance
			move->acceleration = (params.TotalAccelClocks() <= 0)
									 ? 0.0
									 : (float)((params.peakAcceleration * params.TotalAccelClocks() -
												(motioncalc_t)0.5 * params.jerk *
													(Msquare((motioncalc_t)params.phaseClocks[0]) +
													 Msquare((motioncalc_t)params.phaseClocks[2]))) /
											   (params.TotalAccelClocks() * params.totalDistance));
			move->deceleration = (params.TotalDecelClocks() <= 0)
									 ? 0.0
									 : (float)((-params.peakDeceleration * params.TotalDecelClocks() -
												(motioncalc_t)0.5 * params.jerk *
													(Msquare((motioncalc_t)params.phaseClocks[4]) +
													 Msquare((motioncalc_t)params.phaseClocks[6]))) /
											   (params.TotalDecelClocks() * params.totalDistance));
		}
		else
		{
			move->acceleration = (float)(params.peakAcceleration /
										 params.totalDistance); // scale the acceleration to correspond to unit distance
			move->deceleration =
				-(float)(params.peakDeceleration /
						 params.totalDistance); // scale the deceleration to correspond to unit distance
		}
#  else
		move->acceleration = (float)(params.acceleration /
									 params.totalDistance); // scale the acceleration to correspond to unit distance
		move->deceleration = -(float)(params.deceleration /
									  params.totalDistance); // scale the deceleration to correspond to unit distance
#  endif
		move->extruderDrives = 0;
		move->numDrivers = canDriver.localDriver + 1;
		move->zero1 = move->zero2 = 0;
		move->useLateInputShaping = params.useInputShaping;

		// Clear out the per-drive fields. Can't use a range-based FOR loop on a packed struct.
		// NOLINTNEXTLINE(modernize-loop-convert) - binding a reference to a packed field is ill-formed
		for (size_t drive = 0; drive < ARRAY_SIZE(move->perDrive); ++drive)
		{
			move->perDrive[drive].Init();
		}
	}
	else if (canDriver.localDriver >= buf->msg.moveLinearShaped.numDrivers)
	{
		buf->msg.moveLinearShaped.numDrivers = canDriver.localDriver + 1;
	}
	return buf;
}

void CanMotion::AddAxisMovement(const PrepParams& params, DriverId canDriver, int32_t steps) noexcept
{
	CanMessageBuffer* const _ecv_null buf = GetBuffer(params, canDriver);
	if (buf != nullptr)
	{
		buf->msg.moveLinearShaped.perDrive[canDriver.localDriver].steps = steps;
	}
}

void CanMotion::AddExtruderMovement(const PrepParams& params,
									DriverId canDriver,
									float extrusion,
									bool usePressureAdvance) noexcept
{
	CanMessageBuffer* const _ecv_null buf = GetBuffer(params, canDriver);
	if (buf != nullptr)
	{
		buf->msg.moveLinearShaped.perDrive[canDriver.localDriver].extrusion = extrusion;
		buf->msg.moveLinearShaped.extruderDrives |= 1u << canDriver.localDriver;
		buf->msg.moveLinearShaped.usePressureAdvance = usePressureAdvance;
	}
}

uint32_t CanMotion::FinishMovement(uint32_t moveStartTime, bool simulating, bool checkEndstops) noexcept
{
	uint32_t clocks = 0;
	if (simulating)
	{
		FreeMovementBuffers(); // it turned out that there was nothing to move
	}
	else
	{
		CanMessageBuffer* _ecv_null buf = movementBufferList;
		if (buf != nullptr)
		{
			const MutexLocker lock((checkEndstops) ? &stopListMutex : nullptr);
			do
			{
				CanMessageBuffer* const _ecv_null nextBuffer =
					buf->next; // must get this before sending the buffer, because sending the buffer releases it
				CanMessageMovementLinearShaped& msg = buf->msg.moveLinearShaped;
				if (msg.HasMotion())
				{
					msg.whenToExecute = moveStartTime;
					uint8_t& seq = nextSeq[buf->id.Dst()];
					msg.seq = seq;
					seq = (seq + 1) & 0x7F;
					buf->dataLength = msg.GetActualDataLength();
					if (checkEndstops)
					{
						// Set up the stop list
						auto* const sl = new DriversStopList(stopList, buf->id.Dst());
						const size_t nd = msg.numDrivers;
						sl->numDrivers = (uint8_t)nd;
						for (size_t i = 0; i < nd; ++i)
						{
							sl->stopStates[i] =
								(msg.perDrive[i].steps != 0) ? DriverStopState::Active : DriverStopState::Inactive;
						}
						stopList = sl;
					}
					CanInterface::SendMotion(buf); // queues the buffer for sending and frees it when done
					clocks = currentMoveClocks;
				}
				else
				{
					CanMessageBuffer::Free(buf);
				}
				buf = nextBuffer;
			} while (buf != nullptr);

			movementBufferList = nullptr;
		}
	}
	return clocks;
}

#  if HAS_SBC_INTERFACE

namespace CanMotion
{
	// The move being accumulated from ScheduleMove packets, and how many drivers it has so far.
	// A move split across several packets shares one moveId; anything held when a different id
	// arrives belonged to a move the SBC abandoned part way through and must not reach the boards.
	static uint32_t sbcMoveId = 0;
	static bool sbcMoveInProgress = false;

	// Which input stops which driver, for the move being accumulated or executed. One entry per
	// driver that watches something, which is at most the drivers a move can carry.
	//
	// Only the stop is decided here. Where the drive should end up is worked out on the SBC, which
	// already evaluates the same motion to report live positions (Motion::DriveTracker), so the
	// velocity profile is not duplicated on this side.
	//
	// SbcProtocol::DriverStopWatch rather than a struct of our own, because the rule that matches a
	// trigger against these is shared with the host-side tests. Holding the tested type as our own
	// state is what stops the two drifting; see DuetSpiProtocol/StopRules.h
	static std::array<SbcProtocol::DriverStopWatch, SbcProtocol::MaxMoveDrivers> endstopWatches;
	static size_t numEndstopWatches = 0;

	// The inputs known to be active, so that a move armed on one that is already active can be
	// stopped before it starts.
	//
	// Sized like the watches above, because that is the same bound: a move names at most one input
	// per driver it carries, so a store this size can never be short of the input a driver needs.
	// Only the kinds a move can be stopped by are held, so nothing else can take up the room.
	//
	// SbcProtocol::NoteInputState decides what is held and what is not, for the same reason the
	// watches above use SbcProtocol::DriverStopWatch: it is the shared, host-tested rule.
	//
	// This holds only what the boards have reported since startup. An input that was already active
	// before the first change arrived is unknown here, which is the SBC's to answer: the reply to
	// CanMessageCreateInputMonitor carries the level, and that is what seeds sensors.endstops[].
	static std::array<SbcProtocol::ActiveInput, SbcProtocol::MaxMoveDrivers> activeInputs;
	static size_t numActiveInputs = 0;

	// Stop the drivers of the move being accumulated whose input is already active, filling in the
	// move they belong to. Returns how many were stopped
	static size_t StopDriversWatchingActiveInputs(std::span<SbcProtocol::MotionStoppedDriver> stopped,
												 uint32_t& moveId) noexcept
	{
		size_t numStopped = 0;
		for (size_t i = 0; i < numActiveInputs && numStopped < stopped.size(); ++i)
		{
			// Zero for the reading: only a stall names drivers in one, and a stall is never held here
			numStopped += StopDriversWatchingInput(activeInputs[i].board, activeInputs[i].handle, 0,
												   stopped.subspan(numStopped), moveId);
		}
		return numStopped;
	}

} // namespace CanMotion

void CanMotion::NoteInputState(uint8_t inputBoard, uint16_t inputHandle, bool active) noexcept
{
	numActiveInputs = SbcProtocol::NoteInputState(std::span{activeInputs}, numActiveInputs, inputBoard, inputHandle,
												  active);
}

void CanMotion::ScheduleFromSbc(const SbcProtocol::ScheduleMoveHeader& header,
								std::span<const SbcProtocol::ScheduleMoveDriver> drivers) noexcept
{
	if (!sbcMoveInProgress || header.moveId != sbcMoveId)
	{
		// Either the first packet of a move, or the first of a new one while an old one is still
		// held. StartMovement frees whatever was accumulated, which is what makes the second case
		// safe: half a move must never be sent.
		StartMovement();
		sbcMoveId = header.moveId;
		sbcMoveInProgress = true;
		numEndstopWatches = 0;			// the watches belong to the move being abandoned, not the new one
	}

	// Rebuild PrepParams from the packet. The SBC plans second-order moves - it has no S-curve
	// support - so when this firmware is built with S-curve on, the profile goes into the seven-phase
	// form with jerk zero and only the constant-acceleration phases used. That is the same shape
	// PrepParams::SetFromDDA produces for a second-order move, and GetBuffer already special-cases
	// jerk == 0, so nothing downstream can tell the difference.
	PrepParams params{};
	params.totalDistance = (motioncalc_t)header.totalDistance;
	params.startSpeed = (motioncalc_t)header.startSpeed;
	params.topSpeed = (motioncalc_t)header.topSpeed;
	params.endSpeed = (motioncalc_t)header.endSpeed;
	params.useInputShaping = (header.flags & ScheduleMoveFlags::UseInputShaping) != 0;

	const auto accelDistance = (motioncalc_t)header.accelDistance;
	const auto decelStartDistance = (motioncalc_t)header.decelStartDistance;

#    if SUPPORT_S_CURVE
	params.jerk = (motioncalc_t)0.0;					// signals that this is not an S-curve move
	params.peakAcceleration = params.initialAcceleration = (motioncalc_t)header.acceleration;
	params.peakDeceleration = params.initialDeceleration = (motioncalc_t)header.deceleration;
	params.phaseClocks[0] = params.phaseClocks[2] = params.phaseClocks[4] = params.phaseClocks[6] = 0;
	params.phaseClocks[1] = header.accelClocks;
	params.phaseClocks[3] = header.steadyClocks;
	params.phaseClocks[5] = header.decelClocks;
	params.distances[0] = params.distances[2] = params.distances[4] = params.distances[6] = (motioncalc_t)0.0;
	params.distances[1] = accelDistance;
	params.distances[3] = decelStartDistance - accelDistance;
	params.distances[5] = params.totalDistance - decelStartDistance;
	params.speedsCalculated = false;
#    else
	params.accelClocks = header.accelClocks;
	params.steadyClocks = header.steadyClocks;
	params.decelClocks = header.decelClocks;
	params.acceleration = (motioncalc_t)header.acceleration;
	params.deceleration = (motioncalc_t)header.deceleration;
	params.accelDistance = accelDistance;
	params.decelStartDistance = decelStartDistance;
#    endif

	// Iterating the span rather than header.numDrivers: the count and the records arrive in the same
	// packet, so trusting the count to describe the records is trusting the packet to be consistent
	// with itself. The caller sized the span from what the payload actually carries.
	const bool usePressureAdvance = (header.flags & ScheduleMoveFlags::UsePressureAdvance) != 0;
	for (const SbcProtocol::ScheduleMoveDriver& d : drivers)
	{
		const DriverId driver(d.boardAddress, d.driverNumber);
		if (d.isExtruder != 0)
		{
			AddExtruderMovement(params, driver, d.extrusion, usePressureAdvance);
		}
		else
		{
			AddAxisMovement(params, driver, d.steps);
		}

		// Record what stops this driver, if anything. Only an endstop move carries these.
		//
		// The array is sized for a whole move rather than a packet, because the count is only reset
		// when a new move starts. Dropping a watch is not a small loss: that driver is then stopped
		// by nothing and runs to the end of its commanded travel, so it is reported rather than
		// quietly skipped
		if (d.stopOnBoard != SbcProtocol::NoEndstopBoard)
		{
			if (numEndstopWatches < endstopWatches.size())
			{
				endstopWatches[numEndstopWatches] = { d.boardAddress, d.driverNumber, d.stopOnBoard, d.stopOnHandle,
													  d.stopGroup,   d.stopAction,   true };
				++numEndstopWatches;
			}
			else
			{
				reprap.GetPlatform().MessageF(ErrorMessage,
											  "move %" PRIu32 ": driver %u.%u will not be stopped by its endstop, "
											  "because this move watches more than %u drivers\n",
											  header.moveId, d.boardAddress, d.driverNumber,
											  (unsigned int)endstopWatches.size());
			}
		}
	}

	if ((header.flags & ScheduleMoveFlags::LastPacket) != 0)
	{
		// Every driver of the move is recorded now, so an input that is already active can be applied
		// to it. This is the only chance to: a board reports an input when it changes, so one that
		// closed while this move was on its way here will not be reported again, and one closed
		// before the SBC decided what to watch is already in the level it read
		SbcProtocol::MotionStoppedDriver stopped[SbcProtocol::MaxMotionStoppedDrivers];
		uint32_t moveId = 0;
		const size_t numStopped = StopDriversWatchingActiveInputs(std::span{stopped}, moveId);

		sbcMoveInProgress = false;
		(void)FinishMovement(header.whenToExecute,
							 false, // the SBC does not send a move it is only simulating
							 (header.flags & ScheduleMoveFlags::CheckEndstops) != 0);

		if (numStopped != 0)
		{
			// Zero for the trigger time: the input was active before the move started, so there is
			// no overshoot to wind back and the drives are where the SBC will find them. What the
			// report is for is the SBC learning the endstop was reached, without which the move ends
			// as one that watched something and saw nothing
			reprap.GetSbcInterface().ReportMotionStopped(0, moveId, std::span{stopped, numStopped});
		}
	}
}

#  endif

bool CanMotion::CanPrepareMove() noexcept
{
	return CanMessageBuffer::GetFreeBuffers() >= MaxCanBoards;
}

// This is called by the CanSender task to check if we have any urgent messages to send
// The only urgent messages we may have currently are messages to stop drivers, or to tell them that all drivers have
// now been stopped and they need to revert to the requested stop position.
CanMessageBuffer* _ecv_null CanMotion::GetUrgentMessage() noexcept
{
	const MutexLocker lock(stopListMutex);	// make sure the list isn't being changed while we traverse it

	// The links won't change while we hold the mutex, but the receiver task may still move a driver
	// to StopRequested as we scan
	for (DriversStopList* _ecv_null sl = stopList; sl != nullptr; sl = sl->next)
	{
		uint16_t driversToStop = 0;
		for (size_t driver = 0; driver < sl->numDrivers; ++driver)
		{
			if (sl->stopStates[driver] == DriverStopState::StopRequested)
			{
				driversToStop |= 1u << driver;
				sl->stopStates[driver] = DriverStopState::StopSent;
			}
		}

		if (driversToStop != 0)
		{
			auto stopMsg = urgentMessageBuffer.SetupRequestMessageNoRid<CanMessageStopMovement>(
				CanInterface::GetCanAddress(), sl->boardAddress);
			stopMsg->whichDrives = driversToStop;
			return &urgentMessageBuffer;
		}
	}

	return nullptr;
}

// The next 4 functions may be called from the step ISR, so they can't send CAN messages directly

// Flag a CAN-connected driver as not moving when we haven't sent the movement message yet
bool CanMotion::StopDriverWhenProvisional(DriverId driver) noexcept
{
	// Search for the correct movement buffer
	CanMessageBuffer* _ecv_null buf = movementBufferList;
	while (buf != nullptr)
	{
		if (buf->id.Dst() == driver.boardAddress)
		{
			// The move was found so set the steps to zero. We still send the message so that the drivers get enabled.
			buf->msg.moveLinearShaped.perDrive[driver.localDriver].steps = 0;
			return true;
		}
		buf = buf->next;
	}
	return false;
}

#  if HAS_SBC_INTERFACE

size_t CanMotion::StopDriversWatchingInput(uint8_t inputBoard, uint16_t inputHandle, uint32_t reading,
										   std::span<SbcProtocol::MotionStoppedDriver> stopped,
										   uint32_t& moveId) noexcept
{
	// Read here rather than by a second call from the caller: the drivers stopped and the move they
	// belong to are one answer, and a move scheduled in between would make two calls disagree
	moveId = sbcMoveId;

	// Which watch this trigger matched and what that watch stops - itself, its whole drive, or the
	// whole move. Both are decided by DuetSpiProtocol/StopRules.h, which is where this rule can be
	// tested; everything below is the acting on it
	const std::span<const SbcProtocol::DriverStopWatch> watches{ endstopWatches.data(), numEndstopWatches };
	const SbcProtocol::StopDecision decision =
		SbcProtocol::DecideStop(watches, inputBoard, inputHandle, reading);
	if (decision.action == SbcProtocol::StopAction::none)
	{
		return 0;
	}

	size_t numStopped = 0;
	for (size_t i = 0; i < numEndstopWatches; ++i)
	{
		SbcProtocol::DriverStopWatch& watch = endstopWatches[i];
		if (!watch.stillRunning || !SbcProtocol::StopsDriver(watches, decision, i))
		{
			continue;
		}

		// Recorded before the stop is attempted, because what it feeds is the escalation: a driver
		// stopped individually has to stop counting towards its group, or the last motor of a
		// gantry squaring itself would never escalate to stopping the axis
		watch.stillRunning = false;

		const DriverId driver(watch.driverBoard, watch.driverNumber);
		bool didStop = false;
		if (sbcMoveInProgress)
		{
			// The move has not gone out yet, so the driver can simply be given no steps. This is the
			// case RepRapFirmware calls an endstop already triggered at the start of the move. It is
			// still reported: the drive needs no correction because it never moved, but the SBC has
			// no other way to learn that the axis reached its endstop
			didStop = StopDriverWhenProvisional(driver);
		}
		else
		{
			// The move is running on the boards. Stopping it is this side's whole job; where the
			// drive should end up is the SBC's, which is why it is reported below
			didStop = StopDriverWhenExecuting(driver);
		}

		if (didStop && numStopped < stopped.size())
		{
			stopped[numStopped].boardAddress = watch.driverBoard;
			stopped[numStopped].driverNumber = watch.driverNumber;
			stopped[numStopped].padding = 0;
			++numStopped;
		}
	}
	return numStopped;
}

#  endif

// Tell a CAN-connected driver to stop moving after we have sent the movement message.
// Return true if we found it, we hadn't already requested a stop, and now we have.
bool CanMotion::StopDriverWhenExecuting(DriverId driver) noexcept
{
	DriversStopList* _ecv_null sl = stopList;
	while (sl != nullptr)
	{
		if (sl->boardAddress == driver.boardAddress)
		{
			if (driver.localDriver < sl->numDrivers &&
				sl->stopStates[driver.localDriver] == DriverStopState::Active) // if active and stop not yet requested
			{
				sl->stopStates[driver.localDriver] = DriverStopState::StopRequested;
				return true;
			}
			break; // we found the right board, no point in searching further
		}
		sl = sl->next;
	}
	return false;
}

#endif

// End
