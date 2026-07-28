/*
 * CanMotionShim.cpp
 *
 * The implementation of Compat/CAN/CanMotion.h: forwards the firmware's CanMotion calls to the
 * ScheduleMoveBuilder the motion system owns. See that header for why the indirection exists.
 */

#include <CAN/CanMotion.h>

#include <Movement/DDA.h>
#include <Platform/RepRap.h>

namespace
{
	Duet::Sbc::Motion::ScheduleMoveBuilder& Builder() noexcept
	{
		return reprap.GetMove().GetScheduleMoveBuilder();
	}
}

void CanMotion::StartMovement() noexcept
{
	Builder().StartMovement();
}

void CanMotion::AddAxisMovement(const PrepParams& params, DriverId canDriver, int32_t steps) noexcept
{
	Builder().AddAxisMovement(params, canDriver, steps);
}

void CanMotion::AddExtruderMovement(const PrepParams& params, DriverId canDriver, float extrusion,
									bool usePressureAdvance) noexcept
{
	Builder().AddExtruderMovement(params, canDriver, extrusion, usePressureAdvance);
}

uint32_t CanMotion::FinishMovement(const DDA& dda, uint32_t moveStartTime, bool simulating) noexcept
{
	return Builder().FinishMovement(dda.GetMoveId(), moveStartTime, simulating, dda.IsCheckingEndstops(),
									dda.UsesInputShaping());
}

bool CanMotion::CanPrepareMove() noexcept
{
	return Builder().CanPrepareMove();
}
