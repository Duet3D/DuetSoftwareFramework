/*
 * Tasks.h - compatibility shim
 *
 * The firmware allocates DDAs and MoveSegments from a permanent arena carved out of the top of RAM
 * and never frees them - see the operator new / no-op operator delete pairs in Movement/DDA.h and
 * Movement/MoveSegment.h. That is not only a memory-fragmentation measure: it keeps the general
 * allocator off the motion path.
 *
 * The same reasoning applies here for a different reason. The motion thread runs SCHED_FIFO, so any
 * allocation that could take glibc's malloc arena lock, or fault in a page, is a source of the
 * latency this whole native component exists to avoid. So AllocPermanent is a bump pointer over one
 * mmap'd, mlock'd, pre-faulted region, and freeing is not supported.
 */

#ifndef SRC_COMPAT_PLATFORM_TASKS_H_
#define SRC_COMPAT_PLATFORM_TASKS_H_

#include <RepRapFirmware.h>

#include <new>		// for std::align_val_t

namespace Tasks
{
	// Reserve the arena. Call once, before anything allocates from it. Returns false if the region
	// could not be mapped; mlock failing is not fatal (it needs privileges the caller may not have)
	// but is reported through the log sink, because without it a page fault can stall a move.
	bool InitPermanentArena(size_t bytes) noexcept;

	// Release the arena. Only for tests, which build up and tear down rings repeatedly; anything
	// allocated from it dangles afterwards.
	void ReleasePermanentArena() noexcept;

	// Allocate from the arena. Never returns null: exhaustion means the arena was sized wrongly,
	// which is a configuration error rather than a runtime condition to recover from, so it aborts.
	void *AllocPermanent(size_t count) noexcept;
	void *AllocPermanent(size_t count, std::align_val_t align) noexcept;

	// Bytes still unallocated. DDARing::Init uses this to decide how many DDAs it can afford.
	ptrdiff_t GetNeverUsedRam() noexcept;
}

#endif /* SRC_COMPAT_PLATFORM_TASKS_H_ */
