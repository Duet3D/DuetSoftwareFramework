/*
 * rrf-Move-AddLinearSegments.cpp
 *
 * Builds the MoveSegment chains that DriveTracker walks to report near-real-time positions of the
 * CAN-connected drives. This is Move::AddSegment / Move::AddLinearSegments lifted verbatim out of
 * the imported RepRapFirmware Move.cpp (lines 1702-2247) before that file was deleted.
 *
 * NOT COMPILED (see reference/README.md). Porting it (dropping the S-curve
 * branches and the AxisShaper-dependent shaped path, replacing BasePriorityBooster) is step 5.
 */


#if SUPPORT_S_CURVE

// Calculate the initial speed given the duration, distance, acceleration and jerk
static inline motioncalc_t CalcInitialSpeed(uint32_t duration, motioncalc_t distance, motioncalc_t a, motioncalc_t j) noexcept
{
	return distance/(motioncalc_t)duration - (OneHalf * a + OneSixth * j * (motioncalc_t)duration) * (motioncalc_t)duration;
}

#else

// Calculate the initial speed given the duration, distance and acceleration
static inline motioncalc_t CalcInitialSpeed(uint32_t duration, motioncalc_t distance, motioncalc_t a) noexcept
{
	return distance/(motioncalc_t)duration - OneHalf * a * (motioncalc_t)duration;
}

#endif

// Add a segment into a segment list, which may be empty.
// If the list is not empty then the new segment may overlap segments already in the list.
// The units of the input parameters are steps for distance and step clocks for time.
MoveSegment *Move::AddSegment(MoveSegment *list, uint32_t startTime, uint32_t duration, motioncalc_t distance, motioncalc_t a,
#if SUPPORT_S_CURVE
	 	 	 	 	 	 	 	 motioncalc_t j, MovementFlags moveFlags, motioncalc_t pressureAdvanceClocks
#else
								 	 	 	 	 MovementFlags moveFlags, motioncalc_t pressureAdvanceClocksTimesDuration
#endif
							) noexcept
{
	if ((int32_t)duration <= 0)
	{
		const StringRef& dbgRef = Platform::genericDebugBuffer.GetRef();
		dbgRef.printf("Adding zero or negative duration segment: d=%3e a=%.3e\n", (double)distance, (double)a);
		Platform::hasGenericDebug = true;
	}

#if SUPPORT_S_CURVE
	// Adjust the distance and acceleration (and implicitly the initial speed) to account for pressure advance
	distance += (a + (motioncalc_t)0.5 * j * (motioncalc_t)duration) * pressureAdvanceClocks * (motioncalc_t)duration;
	a += j * pressureAdvanceClocks;
#else
	// Adjust the distance (and implicitly the initial speed) to account for pressure advance
	distance += a * pressureAdvanceClocksTimesDuration;
#endif

#if !SEGMENT_DEBUG
	if (reprap.GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::Segments))
#endif
	{
#if SUPPORT_S_CURVE
		debugPrintf("Add seg: st=%" PRIu32 " t=%7" PRIu32 " dist=%9.3f u=%10.4e a=%10.4e j=%10.4e f=x%02" PRIx32 "\n",
					startTime, duration, (double)distance, (double)CalcInitialSpeed(duration, distance, a, j), (double)a, (double)j, moveFlags.all);
#else
		debugPrintf("Add seg: st=%" PRIu32 " t=%7" PRIu32 " dist=%9.3f u=%10.4e a=%10.4e f=x%02" PRIx32 "\n",
					startTime, duration, (double)distance, (double)CalcInitialSpeed(duration, distance, a), (double)a, moveFlags.all);
#endif
	}

	MoveSegment *_ecv_null prev = nullptr;
	MoveSegment *_ecv_null seg = list;

	// Loop until we find the earliest existing segment that the new one will come before (i.e. new one starts before existing one start) or will overlap (i.e. the new one starts before the existing segment ends)
	while (seg != nullptr)
	{
		int32_t offset = (int32_t)(startTime - seg->GetStartTime());			// how much later the segment we want to add starts after the existing one starts
		if (offset < 0)															// if the new segment starts before the existing one starts
		{
			if (offset + (int32_t)duration <= 0)
			{
				break;															// new segment fits entirely before the existing one
			}
#if AVOID_SHORT_SEGMENTS
			if (offset >= -MoveSegment::MinDuration && duration >= 10 * (uint32_t)MoveSegment::MinDuration)	// if it starts only slightly earlier and we can reasonably shorten it
			{
				startTime = seg->GetStartTime();								// then just delay and shorten the new segment slightly, to avoid creating a tiny segment
# if SEGMENT_DEBUG
				debugPrintf("Adjusting(1) t=%" PRIu32 " a=%.4e", duration, (double)a);
# endif
				duration = (uint32_t)((int32_t)duration + offset);
# if SEGMENT_DEBUG
				debugPrintf(" to t=%" PRIu32 " a=%.4e\n", duration, (double)a);
# endif
			}
			else																// new segment starts before the existing one and can't be delayed/shortened so that it doesn't
#endif
			{
				// Insert part of the new segment before the existing one, then merge the rest
				seg = MoveSegment::Allocate(seg);
				const uint32_t firstDuration = -offset;
				const motioncalc_t mFirstDuration = (motioncalc_t)firstDuration;
#if SUPPORT_S_CURVE
				const motioncalc_t firstDistance = (CalcInitialSpeed(duration, distance, a, j) + (OneHalf * a + OneSixth * j * mFirstDuration) * mFirstDuration) * mFirstDuration;
#else
				const motioncalc_t firstDistance = (CalcInitialSpeed(duration, distance, a) + OneHalf * a * mFirstDuration) * mFirstDuration;
#endif
				seg->SetParameters(startTime, firstDuration, firstDistance, a J_ACTUAL_PARAMETER(j), moveFlags);
#if SUPPORT_S_CURVE
				a += j * mFirstDuration;
#endif
				if (prev == nullptr)
				{
					list = _ecv_not_null(seg);
				}
				else
				{
					prev->SetNext(seg);
				}
#if CHECK_SEGMENTS
				CheckSegment(__LINE__, prev);
				CheckSegment(__LINE__, seg);
#endif
				duration -= firstDuration;
				startTime += firstDuration;
				distance -= firstDistance;
				prev = seg;
				seg = seg->GetNext();
				if (seg == nullptr)
				{
					break;
				}
			}
			offset = 0;
		}

		// At this point the new segment starts later or at the same time as the existing one (i.e. offset is non-negative)
		if (offset < (int32_t)seg->GetDuration())													// if new segment starts before the existing one ends
		{
#if AVOID_SHORT_SEGMENTS
			if (offset != 0 && offset + MoveSegment::MinDuration >= (int32_t)seg->GetDuration() && duration >= 10 * (uint32_t)MoveSegment::MinDuration)
			{
				// New segment starts just before the existing one ends, but we can delay and shorten it to start when the existing segment ends
# if SEGMENT_DEBUG
				debugPrintf("Adjusting(3) t=%" PRIu32 " a=%.4e", duration, (double)a);
# endif
				const uint32_t startDelay = seg->GetDuration() - (uint32_t)offset;
				startTime += startDelay;																	// postpone and shorten it a little
				duration -= startDelay;
# if SEGMENT_DEBUG
				debugPrintf(" to t=%" PRIu32 " a=%.4e\n", duration, (double)a);
# endif
				// Go round the loop again
			}
			else
#endif
			{
				// The new segment overlaps the existing one and can't be delayed so that it doesn't.
				// If the new segment starts later than the existing one does, split the existing one.
				if (offset != 0)
				{
					prev = seg;
					seg = seg->Split((uint32_t)offset);
#if CHECK_SEGMENTS
					CheckSegment(__LINE__, prev);
					CheckSegment(__LINE__, seg);
#endif
					offset = 0;
				}

				// The segment we wish to add now starts at the same time as 'seg' but it may end earlier or later than the one at 'seg' does.
				int32_t timeDifference = (int32_t)(duration - seg->GetDuration());
#if AVOID_SHORT_SEGMENTS
				if (timeDifference > 0 && timeDifference <= MoveSegment::MinDuration && duration >= 10 * (uint32_t)MoveSegment::MinDuration)
				{
					// New segment is slightly longer then the old one but it can be shortened
# if SEGMENT_DEBUG
					debugPrintf("Adjusting(3) t=%" PRIu32 " a=%.4e", duration, (double)a);
# endif
					duration -= (uint32_t)timeDifference;
# if SEGMENT_DEBUG
					debugPrintf(" to t=%" PRIu32 " a=%.4e\n", duration, (double)a);
# endif
					timeDifference = 0;
				}
#endif
				if (timeDifference > 0)
				{
					// The existing segment is shorter in time than the new one, so add the new segment in two or more parts
					const motioncalc_t segDuration = (motioncalc_t)seg->GetDuration();
#if SUPPORT_S_CURVE
					const motioncalc_t firstDistance = (CalcInitialSpeed(duration, distance, a, j) + (OneHalf * a + OneSixth * j * segDuration) * segDuration) * segDuration;	// distance moved by the first part of the new segment
#else
					const motioncalc_t firstDistance = (CalcInitialSpeed(duration, distance, a) + OneHalf * a * segDuration) * segDuration;		// distance moved by the first part of the new segment
#endif
#if SEGMENT_DEBUG
					debugPrintf("merge1: ");
#endif
					seg->Merge(firstDistance, a J_ACTUAL_PARAMETER(j), moveFlags);
#if SUPPORT_S_CURVE
					a += j * segDuration;
#endif
#if CHECK_SEGMENTS
					CheckSegment(__LINE__, prev);
					CheckSegment(__LINE__, seg);
#endif
					distance -= firstDistance;
					startTime += seg->GetDuration();
					duration = (uint32_t)timeDifference;
					// Now go round the loop again
				}
				else
				{
					// New segment ends earlier or at the same time as the old one
					if (timeDifference != 0)
					{
						// Split the existing segment in two
						seg->Split(duration);
#if CHECK_SEGMENTS
						CheckSegment(__LINE__, prev);
						CheckSegment(__LINE__, seg);
#endif
					}

					// The new segment and the existing one now have the same start time and duration, so merge them
#if SEGMENT_DEBUG
					debugPrintf("merge2: ");
#endif
					seg->Merge(distance, a J_ACTUAL_PARAMETER(j), moveFlags);
					goto finished;								// ugly but saves some code
				}
			}
		}

		prev = seg;
		seg = seg->GetNext();
	}

	// If we get here then the new segment (or what's left of it) needs to be added before 'seg' which may be null
	{
		MoveSegment *newSeg = MoveSegment::Allocate(seg);
		newSeg->SetParameters(startTime, duration, distance, a J_ACTUAL_PARAMETER(j), moveFlags);
		if (prev == nullptr)
		{
			list = newSeg;
		}
		else
		{
			prev->SetNext(newSeg);
		}
	}

finished:
#if CHECK_SEGMENTS
	CheckSegment(__LINE__, prev);
	CheckSegment(__LINE__, seg);
#endif
#if SEGMENT_DEBUG
	MoveSegment::DebugPrintList(segments);
#endif
	return list;
}

// Add some linear segments to be executed by a driver, taking account of possible input shaping. This is used by linear axes and by extruders.
// We never add a segment that starts earlier than the earliest existing segment (if any).
void Move::AddLinearSegments(size_t logicalDrive, uint32_t startTime, const PrepParams& params, motioncalc_t steps, MovementFlags moveFlags) noexcept
{
#if 0	//debug
	if (reprap.GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::Segments))
	{
//		debugPrintf("AddLin: st=%" PRIu32 " steps=%.1f\n", startTime, (double)steps);
//		params.DebugPrint();
	}
#endif

	DriveMovement& dm = dms[logicalDrive];
	MoveSegment *_ecv_null tail;

	// We need to ensure that while we are amending the segment list, the step ISR doesn't start executing a segment that we are amending.
	// We don't want to disable interrupts during the entire process of adding a segment, because that risks provoking hiccups when we re-enable interrupts and the ISR catches up with the overdue steps.
	// Instead we break off the tail of the segment chain containing the segments we need to change, re-enable interrupts, then modify that tail as needed. At the end we put the tail back.
	{
		MoveSegment *_ecv_null prev = nullptr;

		BasePriorityBooster booster(NvicPriorityStep);					// shut out the step interrupt

		tail = dm.segments;
		while (tail != nullptr)
		{
			const uint32_t segStartTime = tail->GetStartTime();
			const uint32_t endTime = segStartTime + tail->GetDuration();
			if ((int32_t)(startTime - endTime) < 0)										// if the segments we want to add start before this segment ends
			{
				if (tail->GetFlags().executing)
				{
					// Error, the segment we are trying to add overlaps an executing one
					const StringRef& dbgRef = Platform::genericDebugBuffer.GetRef();
					dbgRef.printf("Code 3 move error: new: start=%" PRIu32 " overlap=%" PRIu32 " time now=%" PRIu32 ", existing: ",
									startTime, segStartTime + tail->GetDuration() - startTime, StepTimer::GetMovementTimerTicks());
					tail->AppendDetails(dbgRef);
					dbgRef.cat('\n');
					Platform::shouldTurnOffHeaters = true;
					Platform::hasGenericDebug = true;
					StepErrorHalt();
					return;
				}

				if ((int32_t)(startTime - segStartTime) > 0)
				{
					// Split the existing segment
					prev = tail;
					tail = tail->Split(startTime - segStartTime);
					prev->SetNext(nullptr);
				}
				else
				{
					// Split just before this segment
					if (prev == nullptr)
					{
						dm.segments = nullptr;
					}
					else
					{
						prev->SetNext(nullptr);
					}
				}
				break;
			}

			prev = tail;
			tail = tail->GetNext();
		}
	}

	// Now it's safe to insert/merge new segments into 'tail'
#if SUPPORT_S_CURVE
	const uint32_t accelConstantStartTime = startTime + params.phaseClocks[0];
	const uint32_t accelEndStartTime = accelConstantStartTime + params.phaseClocks[1];
	const uint32_t steadyStartTime = accelEndStartTime + params.phaseClocks[2];
	const uint32_t decelStartTime = steadyStartTime + params.phaseClocks[3];
	const uint32_t decelConstantStartTime = decelStartTime + params.phaseClocks[4];
	const uint32_t decelEndStartTime = decelConstantStartTime + params.phaseClocks[5];
#else
	const uint32_t steadyStartTime = startTime + params.TotalAccelClocks();
	const uint32_t decelStartTime = steadyStartTime + params.steadyClocks;
#endif
	const motioncalc_t totalDistance = (motioncalc_t)params.totalDistance;
	const motioncalc_t stepsPerMm = steps/totalDistance;

	// Phases with zero duration will not get executed and may lead to infinities in the calculations. Avoid introducing them. Keep the total distance correct.
	// When using input shaping we can save some FP multiplications by multiplying the acceleration or deceleration time by the pressure advance just once instead of once per impulse
#if SUPPORT_S_CURVE
	motioncalc_t phase0PressureAdvanceClocks, phase1PressureAdvanceClocks, phase2PressureAdvanceClocks, phase4PressureAdvanceClocks, phase5PressureAdvanceClocks, phase6PressureAdvanceClocks;
	if (moveFlags.isExtruder && !moveFlags.nonPrintingMove)
	{
		params.EnsureSpeedsSet();				// calculate the intermediate speeds

		phase0PressureAdvanceClocks = (params.phaseClocks[0] == 0) ? (motioncalc_t)0.0 : dm.extruderShaper.GetAverageAdvanceClocks(params.startSpeed, params.phase1StartSpeed, steps);
		phase1PressureAdvanceClocks = (params.phaseClocks[1] == 0) ? (motioncalc_t)0.0 : dm.extruderShaper.GetAverageAdvanceClocks(params.phase1StartSpeed, params.phase1EndSpeed, steps);
		phase2PressureAdvanceClocks = (params.phaseClocks[2] == 0) ? (motioncalc_t)0.0 : dm.extruderShaper.GetAverageAdvanceClocks(params.phase1EndSpeed, params.topSpeed, steps);
		phase4PressureAdvanceClocks = (params.phaseClocks[4] == 0) ? (motioncalc_t)0.0 : dm.extruderShaper.GetAverageAdvanceClocks(params.phase5StartSpeed, params.topSpeed, steps);
		phase5PressureAdvanceClocks = (params.phaseClocks[5] == 0) ? (motioncalc_t)0.0 : dm.extruderShaper.GetAverageAdvanceClocks(params.phase5EndSpeed, params.phase5StartSpeed, steps);
		phase6PressureAdvanceClocks = (params.phaseClocks[6] == 0) ? (motioncalc_t)0.0 : dm.extruderShaper.GetAverageAdvanceClocks(params.endSpeed, params.phase5EndSpeed, steps);
	}
	else
	{
		phase0PressureAdvanceClocks = phase1PressureAdvanceClocks = phase2PressureAdvanceClocks = phase4PressureAdvanceClocks = phase5PressureAdvanceClocks = phase6PressureAdvanceClocks = (motioncalc_t)0.0;
	}
#else
	motioncalc_t accelDistance, accelPressureAdvance;
	if (params.accelClocks == 0)
	{
		accelDistance = (motioncalc_t)0.0;
		accelPressureAdvance = (motioncalc_t)0.0;
	}
	else
	{
		accelDistance = (params.decelClocks + params.steadyClocks == 0) ? totalDistance : (motioncalc_t)params.accelDistance;
		accelPressureAdvance = (moveFlags.isExtruder && !moveFlags.nonPrintingMove)
								? (motioncalc_t)params.accelClocks * dm.extruderShaper.GetAverageAdvanceClocks(params.startSpeed, params.topSpeed, steps)
								: (motioncalc_t)0.0;
	}

	motioncalc_t decelDistance, decelPressureAdvance;
	if (params.decelClocks == 0)
	{
		decelDistance = (motioncalc_t)0.0;
		decelPressureAdvance= (motioncalc_t)0.0;
	}
	else
	{
		decelDistance = totalDistance - ((params.steadyClocks == 0) ? accelDistance : (motioncalc_t)params.decelStartDistance);
		decelPressureAdvance = (moveFlags.isExtruder && !moveFlags.nonPrintingMove)
								? (motioncalc_t)params.decelClocks * dm.extruderShaper.GetAverageAdvanceClocks(params.endSpeed, params.topSpeed, steps)
								: (motioncalc_t)0.0;
	}

	const motioncalc_t steadyDistance = (params.steadyClocks == 0) ? (motioncalc_t)0.0 : totalDistance - accelDistance - decelDistance;
#endif

#if STEPS_DEBUG
	{
		AtomicCriticalSectionLocker lock;
		dm.positionRequested += steps;				// currently we compile for C++17 so we can't make this variable atomic
	}
#endif

	if (moveFlags.noShaping)
	{
#if SUPPORT_S_CURVE
		const motioncalc_t scaledJerk = params.jerk * stepsPerMm;
		if (params.phaseClocks[0] != 0)
		{
			tail = AddSegment(tail, startTime, params.phaseClocks[0], params.distances[0] * stepsPerMm, params.initialAcceleration * stepsPerMm, scaledJerk, moveFlags, phase0PressureAdvanceClocks);
		}
		if (params.phaseClocks[1] != 0)
		{
			tail = AddSegment(tail, accelConstantStartTime, params.phaseClocks[1], params.distances[1] * stepsPerMm, params.peakAcceleration * stepsPerMm, (motioncalc_t)0.0, moveFlags, phase1PressureAdvanceClocks);
		}
		if (params.phaseClocks[2] != 0)
		{
			tail = AddSegment(tail, accelEndStartTime, params.phaseClocks[2], params.distances[2] * stepsPerMm, params.peakAcceleration * stepsPerMm, -scaledJerk, moveFlags, phase2PressureAdvanceClocks);
		}
		if (params.phaseClocks[3] != 0)
		{
			tail = AddSegment(tail, steadyStartTime, params.phaseClocks[3], params.distances[3] * stepsPerMm, (motioncalc_t)0.0, (motioncalc_t)0.0, moveFlags, (motioncalc_t)0.0);
		}
		if (params.phaseClocks[4] != 0)
		{
			tail = AddSegment(tail, decelStartTime, params.phaseClocks[4], params.distances[4] * stepsPerMm, params.initialDeceleration * stepsPerMm, -scaledJerk, moveFlags, phase4PressureAdvanceClocks);
		}
		if (params.phaseClocks[5] != 0)
		{
			tail = AddSegment(tail, decelConstantStartTime, params.phaseClocks[5], params.distances[5] * stepsPerMm, params.peakDeceleration * stepsPerMm, (motioncalc_t)0.0, moveFlags, phase5PressureAdvanceClocks);
		}
		if (params.phaseClocks[6] != 0)
		{
			tail = AddSegment(tail, decelEndStartTime, params.phaseClocks[6], params.distances[6] * stepsPerMm, params.peakDeceleration * stepsPerMm, scaledJerk, moveFlags, phase6PressureAdvanceClocks);
		}
#else
		if (params.accelClocks != 0)
		{
			tail = AddSegment(tail, startTime, params.accelClocks, accelDistance * stepsPerMm, params.acceleration * stepsPerMm, moveFlags, accelPressureAdvance);
		}
		if (params.steadyClocks != 0)
		{
			tail = AddSegment(tail, steadyStartTime, params.steadyClocks, steadyDistance * stepsPerMm, (motioncalc_t)0.0, moveFlags, (motioncalc_t)0.0);
		}
		if (params.decelClocks != 0)
		{
			tail = AddSegment(tail, decelStartTime, params.decelClocks, decelDistance * stepsPerMm, params.deceleration * stepsPerMm, moveFlags, decelPressureAdvance);
		}
#endif
	}
	else
	{
		for (size_t index = 0; index < axisShaper.GetNumImpulses(); ++index)
		{
			const motioncalc_t factor = axisShaper.GetImpulseSize(index) * stepsPerMm;
			const uint32_t startDelay = axisShaper.GetImpulseDelay(index);
#if SUPPORT_S_CURVE
			const motioncalc_t scaledJerk = params.jerk * factor;
			if (params.phaseClocks[0] != 0)
			{
				tail = AddSegment(tail, startTime + startDelay, params.phaseClocks[0], params.distances[0] * factor, params.initialAcceleration * factor, scaledJerk, moveFlags, phase0PressureAdvanceClocks);
			}
			if (params.phaseClocks[1] != 0)
			{
				tail = AddSegment(tail, accelConstantStartTime + startDelay, params.phaseClocks[1], params.distances[1] * factor, params.peakAcceleration * factor, (motioncalc_t)0.0, moveFlags, phase1PressureAdvanceClocks);
			}
			if (params.phaseClocks[2] != 0)
			{
				tail = AddSegment(tail, accelEndStartTime + startDelay, params.phaseClocks[2], params.distances[2] * factor, params.peakAcceleration * factor, -scaledJerk, moveFlags, phase2PressureAdvanceClocks);
			}
			if (params.phaseClocks[3] != 0)
			{
				tail = AddSegment(tail, steadyStartTime + startDelay, params.phaseClocks[3], params.distances[3] * factor, (motioncalc_t)0.0, (motioncalc_t)0.0, moveFlags, (motioncalc_t)0.0);
			}
			if (params.phaseClocks[4] != 0)
			{
				tail = AddSegment(tail, decelStartTime + startDelay, params.phaseClocks[4], params.distances[4] * factor, params.initialDeceleration * factor, -scaledJerk, moveFlags, phase4PressureAdvanceClocks);
			}
			if (params.phaseClocks[5] != 0)
			{
				tail = AddSegment(tail, decelConstantStartTime + startDelay, params.phaseClocks[5], params.distances[5] * factor, params.peakDeceleration * factor, (motioncalc_t)0.0, moveFlags, phase5PressureAdvanceClocks);
			}
			if (params.phaseClocks[6] != 0)
			{
				tail = AddSegment(tail, decelEndStartTime + startDelay, params.phaseClocks[6], params.distances[6] * factor, params.peakDeceleration * factor, scaledJerk, moveFlags, phase6PressureAdvanceClocks);
			}
#else
			if (params.accelClocks != 0)
			{
				tail = AddSegment(tail, startTime + startDelay, params.accelClocks, accelDistance * factor, params.acceleration * factor, moveFlags, accelPressureAdvance);
			}
			if (params.steadyClocks != 0)
			{
				tail = AddSegment(tail, steadyStartTime + startDelay, params.steadyClocks, steadyDistance * factor, (motioncalc_t)0.0, moveFlags, (motioncalc_t)0.0);
			}
			if (params.decelClocks != 0)
			{
				tail = AddSegment(tail, decelStartTime + startDelay, params.decelClocks, decelDistance * factor, params.deceleration * factor, moveFlags, decelPressureAdvance);
			}
#endif
		}
	}

	// If there were no segments attached to this DM initially, we need to schedule the interrupt for the new segment at the start of the list.
	// Don't do this until we have added all the segments for this move, because the first segment we added may have been modified and/or split when we added further segments to implement input shaping
	{
		BasePriorityBooster booster(NvicPriorityStep);								// shut out the step interrupt

		// Join the tail back to the end of the segment list
		{
			MoveSegment *_ecv_null ms = dm.segments;
			if (ms == nullptr)
			{
				dm.segments = tail;
				dm.positionAtMoveStart = dm.currentMotorPosition;						// record the start-of-motion position in case we are checking endstops
#if SUPPORT_PHASE_STEPPING
				dm.phaseStepsTakenSinceMoveStart = (motioncalc_t)0.0;
#endif
			}
			else
			{
				while (ms->GetNext() != nullptr)
				{
					ms = ms->GetNext();
				}
				ms->SetNext(tail);
			}
		}

		if (dm.state == DMState::idle)													// if the DM has no segments
		{
			if (dm.ScheduleFirstSegment())
			{
#if SUPPORT_PHASE_STEPPING
				if (dm.state != DMState::phaseStepping)
#endif
				{
					// Always set the direction when starting the first move
					dm.directionChanged = false;
					SetDirection(dm.drive, dm.direction);
				}
				InsertDM(&dm);
				if (activeDMs == &dm && simulationMode == SimulationMode::off)			// if this is now the first DM in the active list
				{
					if (ScheduleNextStepInterrupt())
					{
						Interrupt();
					}
				}
			}
		}
	}		// End of boosted base priority section
}
