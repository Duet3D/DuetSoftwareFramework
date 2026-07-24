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

#  include <General/FreelistManager.h>

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
		bool sentRevertRequest{false};
		volatile DriverStopState stopStates[MaxLinearDriversPerCanSlave]{};
		volatile int32_t stopSteps[MaxLinearDriversPerCanSlave]{};
	};

	static CanMessageBuffer urgentMessageBuffer;
	static CanMessageBuffer* _ecv_null movementBufferList = nullptr;
	static DriversStopList* volatile _ecv_null stopList = nullptr;
	static uint32_t currentMoveClocks;
	static volatile bool revertAll = false;
	static volatile bool revertedAll = false;
	static volatile uint32_t whenRevertedAll;
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

	revertedAll = false;
	revertAll = false;
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

bool CanMotion::CanPrepareMove() noexcept
{
	return CanMessageBuffer::GetFreeBuffers() >= MaxCanBoards;
}

// This is called by the CanSender task to check if we have any urgent messages to send
// The only urgent messages we may have currently are messages to stop drivers, or to tell them that all drivers have
// now been stopped and they need to revert to the requested stop position.
CanMessageBuffer* _ecv_null CanMotion::GetUrgentMessage() noexcept
{
	if (!revertedAll)
	{
		const MutexLocker lock(stopListMutex); // make sure the list isn't being changed while we traverse it

		// We have to be careful of race conditions here. The stop list links won't change while we are scanning it
		// because we hold the mutex, but ISR may change the stop states to StopRequested up until the time at which it
		// changes revertAll from false to true.
		const bool revertingAll = revertAll;
		for (DriversStopList* _ecv_null sl = stopList; sl != nullptr; sl = sl->next)
		{
			if (!sl->sentRevertRequest) // if we've already reverted the drivers on this board, no more to do
			{
				// Set up a reversion message in case we are going to revert the drivers on this board
				auto revertMsg = urgentMessageBuffer.SetupRequestMessageNoRid<CanMessageRevertPosition>(
					CanInterface::GetCanAddress(), sl->boardAddress);
				uint16_t driversToStop = 0;
				uint16_t driversToRevert = 0;
				size_t numDriversReverted = 0;
				for (size_t driver = 0; driver < sl->numDrivers; ++driver)
				{
					const DriverStopState ss = sl->stopStates[driver];
					if (ss == DriverStopState::StopRequested)
					{
						driversToStop |= 1u << driver;
						sl->stopStates[driver] = DriverStopState::StopSent;
					}
					else if (revertingAll && ss == DriverStopState::StopSent)
					{
						driversToRevert |= 1u << driver;
						revertMsg->finalStepCounts[numDriversReverted++] = sl->stopSteps[driver];
					}
				}

				// Stop messages take priority over revert messages
				if (driversToStop != 0)
				{
					auto stopMsg = urgentMessageBuffer.SetupRequestMessageNoRid<CanMessageStopMovement>(
						CanInterface::GetCanAddress(), sl->boardAddress);
					stopMsg->whichDrives = driversToStop;
					// debugPrintf("Stopping drivers %u on board %u\n", driversToStop, sl->boardAddress);
					return &urgentMessageBuffer;
				}

				if (driversToRevert != 0)
				{
					sl->sentRevertRequest = true;
					revertMsg->whichDrives = driversToRevert;
					revertMsg->clocksAllowed = MillisToStepClocks(BasicDriverPositionRevertMillis);
					urgentMessageBuffer.dataLength = CanMessageRevertPosition::GetActualDataLength(numDriversReverted);
					// debugPrintf("Reverting drivers %u by %" PRIi32 " on board %u\n",
					// driversToRevert,revertMsg->finalStepCounts[0], sl->boardAddress);
					return &urgentMessageBuffer;
				}
			}
		}

		// We found nothing to send
		if (revertingAll)
		{
			// All drivers have been stopped and reverted where requested
			whenRevertedAll = millis();
			revertedAll = true;
		}
	}
	return nullptr;
}

// The next 4 functions may be called from the step ISR, so they can't send CAN messages directly

// Flag a CAN-connected driver as not moving when we haven't sent the movement message yet
void CanMotion::StopDriverWhenProvisional(DriverId driver) noexcept
{
	// Search for the correct movement buffer
	CanMessageBuffer* _ecv_null buf = movementBufferList;
	while (buf != nullptr)
	{
		if (buf->id.Dst() == driver.boardAddress)
		{
			// The move was found so set the steps to zero. We still send the message so that the drivers get enabled.
			buf->msg.moveLinearShaped.perDrive[driver.localDriver].steps = 0;
			break;
		}
		buf = buf->next;
	}
}

// Tell a CAN-connected driver to stop moving after we have sent the movement message.
// Return true if we found it, we hadn't already requested a stop, and now we have.
bool CanMotion::StopDriverWhenExecuting(DriverId driver, int32_t netStepsTaken) noexcept
{
	DriversStopList* _ecv_null sl = stopList;
	while (sl != nullptr)
	{
		if (sl->boardAddress == driver.boardAddress)
		{
			if (driver.localDriver < sl->numDrivers &&
				sl->stopStates[driver.localDriver] == DriverStopState::Active) // if active and stop not yet requested
			{
				sl->stopSteps[driver.localDriver] = netStepsTaken; // must assign this one first
				sl->stopStates[driver.localDriver] = DriverStopState::StopRequested;
				return true;
			}
			break; // we found the right board, no point in searching further
		}
		sl = sl->next;
	}
	return false;
}

// Revert any stopped drivers that we haven't already and return true when there are no drivers to revert
bool CanMotion::RevertStoppedDrivers() noexcept
{
	if (!revertAll && !revertedAll) // if not started reverting yet
	{
		revertAll = true;
		CanInterface::WakeAsyncSender();
		return false;
	}
	return !revertAll || (revertedAll && millis() - whenRevertedAll >= TotalDriverPositionRevertMillis);
}

#endif

// End
