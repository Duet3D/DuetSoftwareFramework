/*
 * MoveSegment.cpp
 *
 *  Created on: 26 Feb 2021
 *      Author: David
 */

#include "MoveSegment.h"

// Static members

MoveSegment *_ecv_null MoveSegment::freeList = nullptr;
unsigned int MoveSegment::numCreated = 0;

// Allocate a MoveSegment, from the freelist if possible, else create a new one
MoveSegment *MoveSegment::Allocate(MoveSegment *_ecv_null pNext) noexcept
{
	const auto iflags = IrqSave();
	MoveSegment *_ecv_null ms = freeList;
	if (ms != nullptr)
	{
		freeList = ms->next;
		IrqRestore(iflags);
		ms->next = pNext;
	}
	else
	{
		++numCreated;
		IrqRestore(iflags);
		ms = new MoveSegment(pNext);
	}
	return ms;
}

// Release a MoveSegment
void MoveSegment::ReleaseAll(MoveSegment *_ecv_null item) noexcept
{
	while (item != nullptr)
	{
		MoveSegment *itemToRelease = item;
		item = item->next;
		Release(itemToRelease);
	}
}

void MoveSegment::DebugPrint() const noexcept
{
	DebugPrintf("s=%" PRIu32 " t=%" PRIu32 " d=%.4f u=%.4e a=%.4e"
#if SUPPORT_S_CURVE
				" j=%.4e"
#endif
				" f=%02" PRIx32 "\n",
				startTime, duration, (double)distance, (double)CalcU(), (double)a,
#if SUPPORT_S_CURVE
				(double)j,
#endif
				flags.all);
}

// Append details of this segment to a string buffer
void MoveSegment::AppendDetails(const StringRef& str) const noexcept
{
	str.catf("s=%" PRIu32 " t=%" PRIu32 " d=%.4f u=%.4e a=%.4e"
#if SUPPORT_S_CURVE
				" j=%.4e"
#endif
				" f=%02" PRIx32 "\n",
				startTime, duration, (double)distance, (double)CalcU(), (double)a,
#if SUPPORT_S_CURVE
				(double)j,
#endif
				flags.all);
}

/*static*/ void MoveSegment::DebugPrintList(const MoveSegment *_ecv_null segs) noexcept
{
	if (segs == nullptr)
	{
		DebugPrintf("null seg\n");
	}
	else
	{
		while (segs != nullptr)
		{
			segs->DebugPrint();
			segs = segs->GetNext();
		}
	}
}

// End
