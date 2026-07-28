/*
 * rrf-DriveMovement-position.cpp
 *
 * Reference material for the position-only replacement for DriveMovement. The SBC has no local
 * drivers, so the DMState step-generation machinery in DriveMovement is dead code here; what is
 * needed is the ability to say where a CAN-connected drive is at a given step-clock time.
 *
 * These are the relevant parts of the imported RepRapFirmware DriveMovement, copied out before that
 * file was deleted:
 *   - DriveMovement::GetCurrentPosition   (DriveMovement.h:270)
 *   - DriveMovement::Init                 (DriveMovement.cpp:32)
 *   - DriveMovement::RetireSegment        (DriveMovement.cpp:106)
 *   - DriveMovement::NewSegment           (DriveMovement.cpp:234) - the segment-advance logic that
 *                                          in RRF is driven by the step ISR and here must be driven
 *                                          by the motion thread instead
 *
 * NOT COMPILED (see reference/README.md). Reducing it to DriveTracker is step 6.
 */

// ---------------------------------------------------------------------------------------------
// DriveMovement::GetCurrentPosition - from DriveMovement.h
// ---------------------------------------------------------------------------------------------
			: stepInterval << microstepShift;									// return the interval between steps converted to full steps
}

#endif

/**
 * @brief Get the current position relative to the start of this segment. Units are microsteps and step clocks.
 * @param when step clock time at which to evaluate the motion. Because the function only reads the first segment this should be the current time.
 * @return position of the dm in microsteps
 */
inline float DriveMovement::GetCurrentPosition(uint32_t when) const noexcept
{
	AtomicCriticalSectionLocker lock;										// we don't want 'segments' changing while we do this

	const MoveSegment* const seg = segments;
	if (seg != nullptr)
	{
		int32_t timeSinceStart = (int32_t)(when - seg->GetStartTime());
		if (timeSinceStart >= 0)
		{
			if ((uint32_t)timeSinceStart >= seg->GetDuration())				// if segment should have finished by now
			{
				// We can't get the next segment because that needs `NewSegment()` to be called
				timeSinceStart = seg->GetDuration();
			}

			return (float)((u + 0.5 * seg->GetA() * timeSinceStart) * timeSinceStart
							  + (motioncalc_t)positionAtSegmentStart + distanceCarriedForwards
						  );
		}

		// If we get here then we have been asked for the position before the current segment started
		return (float)((motioncalc_t)positionAtSegmentStart + distanceCarriedForwards);
	}

	// If we get here then no movement is taking place
	return (float)((motioncalc_t)currentMotorPosition + distanceCarriedForwards);
}

// ---------------------------------------------------------------------------------------------
// DriveMovement::Init / RetireSegment / NewSegment - from DriveMovement.cpp
// ---------------------------------------------------------------------------------------------
void DriveMovement::Init(size_t drv) noexcept
{
	drive = (uint8_t)drv;
	state = DMState::idle;
	distanceCarriedForwards = 0.0;
	currentMotorPosition = positionAtSegmentStart = 0;
	movementAccumulator = 0;
	extruderPrinting = isExtruder = false;
#if STEPS_DEBUG
	positionRequested = 0;
#endif
	driversNormallyUsed = driversCurrentlyUsed = driverEndstopsTriggeredAtStart = 0;
	nextDM = nullptr;
	segments = nullptr;
	segmentFlags.Init();

#if SUPPORT_PHASE_STEPPING
	stepMode = StepMode::stepDir;
#endif
	u = (motioncalc_t)0.0;
#if SUPPORT_S_CURVE
	peakDeltaV = peakDeltaA = (motioncalc_t)0.0;
	finalSpeed = finalAcc = (motioncalc_t)0.0;
#endif
}

void DriveMovement::DebugPrint() const noexcept
{
	const char c = (drive < reprap.GetGCodes().GetTotalAxes()) ? reprap.GetGCodes().GetAxisLetters()[drive] : (char)('0' + LogicalDriveToExtruder(drive));

void DriveMovement::RetireSegment(MoveSegment *oldSegment) noexcept
{
	if (retiredSegment != nullptr)
	{
		MoveSegment::Release(retiredSegment);
	}
	retiredSegment = oldSegment;
}

// Set the position of a motor. Only call this when the motor is not moving.
void DriveMovement::SetMotorPosition(int32_t pos) noexcept
{
	if (reprap.GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::PrintTransforms))
	{
		debugPrintf("Changing drive %u pos from %" PRIi32 " to %" PRIi32 "\n", drive, currentMotorPosition, pos);
	}
	currentMotorPosition = positionAtSegmentStart = pos;
#if STEPS_DEBUG
	positionRequested = (float)pos;
#endif
	ClearMovementPending();
	movementAccumulator.store(0);
	extruderPrinting = false;
}


MoveSegment *_ecv_null DriveMovement::NewSegment(uint32_t now) noexcept
{
	positionAtSegmentStart = currentMotorPosition;

	while (true)
	{
		MoveSegment *_ecv_null seg = segments;					// capture volatile variable
		if (seg == nullptr)
		{
			segmentFlags.Init();
			state = DMState::idle;								// if we have been round this loop already then we will have changed the state, so reset it to idle
#if SUPPORT_S_CURVE
			MovementStopped();
#endif
			return nullptr;
		}

		segmentFlags = seg->GetFlags();							// assume we are going to execute this segment, or at least generate an interrupt when it is due to begin

		if ((int32_t)(seg->GetStartTime() - now) > (int32_t)MoveTiming::MaximumMoveStartAdvanceClocks)
		{
			state = DMState::starting;							// the segment is not due to start for a while. To allow it to be changed meanwhile, generate an interrupt when it is due to start.
			driversCurrentlyUsed = 0;							// don't generate a step on that interrupt
			driverEndstopsTriggeredAtStart = 0;					// reset since we will be setting this in DDA::Prepare()
			nextStepTime = seg->GetStartTime();					// this is when we want the interrupt
#if SUPPORT_S_CURVE
			MovementStopped();									// say we have stopped. NewSegment will be called again for this segment when the state changes from 'starting' to something else.
#endif
			return seg;
		}

		// If this segment is very short, merge it into the next one. This improves efficiency and avoids reporting speed/acceleration discontinuities caused by rounding error.
		// Typically we merge a very short segment into a much longer segment, which works well.
		MoveSegment *_ecv_null nextSeg;
		while (   seg->GetDuration() < MinimumExecutingSegmentDuration
			   && (nextSeg = seg->GetNext()) != nullptr
			   && nextSeg->GetFlags().SameStaticFlags(segmentFlags)
			   && nextSeg->GetStartTime() == seg->GetStartTime() + seg->GetDuration()
			  )
		{
			// We can and should merge this segment into the next one. When the segment is executed, the initial speed will be adjusted to match them.
			nextSeg->CombinePrevious(seg);
			segments = nextSeg;
			MoveSegment::Release(seg);							// release the segment, don't retire it
			seg = nextSeg;
		}

		seg->SetExecuting();
#if SUPPORT_S_CURVE
		UpdateSpeedAndAccelerationChange(seg->CalcU(), seg->GetSpeedChange(), seg->GetA(), seg->GetAccChange());
#else
		u = seg->CalcU(); // used for GetCurrentPosition()
#endif

		// Calculate the movement parameters
		netStepsThisSegment = (int32_t)(seg->GetLength() + distanceCarriedForwards);

#if SUPPORT_PHASE_STEPPING || SUPPORT_CLOSED_LOOP
		if (IsPhaseStepEnabled())
		{
			state = DMState::phaseStepping;
			return seg;
		}
#endif

		bool newDirection;
		int32_t multiplier;
		motioncalc_t rawP;

		if (seg->NormaliseAndCheckLinear(distanceCarriedForwards, t0))
		{
			// Segment is linear
			rawP = seg->CalcLinearRecipU();
			newDirection = !std::signbit(seg->GetLength());
			multiplier = 2 * (int32_t)newDirection - 1;			// +1 or -1
			reverseStartStep = segmentStepLimit = 1 + netStepsThisSegment * multiplier;
			q = 0.0;											// to make the debug output consistent
			state = DMState::cartLinear;
		}
		else
		{
			// Segment has acceleration or deceleration
			// n = distanceCarriedForwards + u * t + 0.5 * a * t^2
			// Therefore 0.5 * t^2 + u * t/a + (distanceCarriedForwards - n)/a = 0
			// Therefore t = -u/a +/- sqrt((u/a)^2 - 2 * (distanceCarriedForwards - n)/a)
			// Calculate the t0, p and q coefficients for an accelerating or decelerating move such that t = t0 + sqrt(p*n + q) and set up the initial direction
			newDirection = !std::signbit(seg->GetA());			// assume accelerating motion
			multiplier = 2 * (int32_t)newDirection - 1;			// +1 or -1
			if (t0 <= (motioncalc_t)0.0)
			{
				// The direction reversal is in the past so the initial direction is the direction of the acceleration
				segmentStepLimit = reverseStartStep = 1 + netStepsThisSegment * multiplier;
				state = DMState::cartAccel;
			}
			else
			{
				// The initial direction is opposite to the acceleration
				newDirection = !newDirection;
				multiplier = -multiplier;
				const int32_t netStepsInInitialDirection = netStepsThisSegment * multiplier;

				if (t0 < (motioncalc_t)seg->GetDuration())
				{
					// Reversal is potentially in this segment, but it may be before the first step, or may be beyond the last step we are going to take
					// It can also happen that the target end speed is zero but due to FP rounding error, distanceToReverse was just below netStepsInInitialDirection and got rounded down
					// Note, t0 = -u/a therefore u = a*t0 therefore u*t0^2 + 0.5*a*t0^2 = -a*t0^2 + 0.5*a*t0^2 = -0.5*a*t0^2
					const motioncalc_t rawDistanceToReverse = (motioncalc_t)-0.5 * seg->GetA() * msquare(t0) + distanceCarriedForwards;
					const motioncalc_t distanceToReverse = rawDistanceToReverse * multiplier;
					const int32_t stepsBeforeReverse = (int32_t)(distanceToReverse - (motioncalc_t)0.2);			// don't step and immediately step back again
					// Note, stepsBeforeReverse may be negative at this point
					if (stepsBeforeReverse <= netStepsInInitialDirection && netStepsInInitialDirection >= 0)
					{
						segmentStepLimit = reverseStartStep = 1 + netStepsInInitialDirection;
						state = DMState::cartDecelNoReverse;
					}
					else if (stepsBeforeReverse <= 0)
					{
						// Reversal happens immediately
						newDirection = !newDirection;
						multiplier = -multiplier;
						segmentStepLimit = reverseStartStep = 1 - netStepsInInitialDirection;
						state = DMState::cartAccel;
					}
					else
					{
						reverseStartStep = stepsBeforeReverse + 1;
						segmentStepLimit = 2 * reverseStartStep - netStepsInInitialDirection - 1;
						state = DMState::cartDecelForwardsReversing;
					}
				}
				else
				{
					// Reversal doesn't occur until after the end of this segment
					segmentStepLimit = reverseStartStep = netStepsInInitialDirection + 1;
					state = DMState::cartDecelNoReverse;
				}
			}
			rawP = (motioncalc_t)2.0/seg->GetA();
			q = msquare(t0) - rawP * distanceCarriedForwards;
#if 0
			if (std::isinf(q))
			{
				debugPrintf("t0=%.1f mult=%.1f dcf=%.3e a=%.4e\n", (double)t0, (double)multiplier, (double)distanceCarriedForwards, (double)seg->GetA());
			}
#endif
		}

		p = rawP * multiplier;

		nextStep = 1;
		if (nextStep < segmentStepLimit)
		{
			if (newDirection != direction)
			{
				directionChanged = true;
				direction = newDirection;
			}

			// Unless we're possibly in the middle of a homing move, re-enable all drivers for this axis
			if (!segmentFlags.checkEndstops)
			{
				driversCurrentlyUsed = driversNormallyUsed;
			}

			if (segmentFlags.isExtruder)
			{
				if (segmentFlags.nonPrintingMove)
				{
					extruderPrinting = false;
				}
				else if (!extruderPrinting)
				{
					extruderPrintingSince = millis();
					extruderPrinting = true;
				}
			}

#if 0	//DEBUG
			debugPrintf("New cart seg: state %u q=%.4e t0=%.4e p=%.4e ns=%" PRIi32 " ssl=%" PRIi32 "\n",
							(unsigned int)state, (double)q, (double)t0, (double)p, nextStep, segmentStepLimit);
#endif
			return seg;
		}

#if 0
		if (netStepsThisSegment != 0)
		{
