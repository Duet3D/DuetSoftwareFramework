/*
 * MoveSegment.cpp
 *
 *  Created on: 26 Feb 2021
 *      Author: David
 */

#include "MoveSegment.h"

// Static members

MoveSegment* MoveSegment::s_freeList = nullptr;
unsigned int MoveSegment::s_numCreated = 0;

// Allocate a MoveSegment, from the freelist if possible, else create a new one
MoveSegment* MoveSegment::Allocate(MoveSegment* pNext) noexcept
{
	MoveSegment* ms = s_freeList;
	if (ms != nullptr)
	{
		s_freeList = ms->m_next;
		ms->m_next = pNext;
	}
	else
	{
		++s_numCreated;
		ms = new MoveSegment(pNext);
	}
	return ms;
}

// Release a MoveSegment
void MoveSegment::ReleaseAll(MoveSegment* item) noexcept
{
	while (item != nullptr)
	{
		MoveSegment* itemToRelease = item;
		item = item->m_next;
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
				m_startTime,
				m_duration,
				(double)m_distance,
				(double)CalcU(),
				(double)m_a,
#if SUPPORT_S_CURVE
				(double)m_j,
#endif
				m_flags.all);
}

/*static*/ void MoveSegment::DebugPrintList(const MoveSegment* segs) noexcept
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
