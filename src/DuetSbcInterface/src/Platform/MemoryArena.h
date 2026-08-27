/*
 * MemoryArena.h
 *
 * The one region every long-lived motion object is allocated from.
 *
 * DDAs and MoveSegments are allocated once and never freed - see the operator new / no-op
 * operator delete pairs in Motion/DDA.h and Motion/MoveSegment.h. In RepRapFirmware that is a
 * memory-fragmentation measure as much as anything; here the reason is latency. The motion thread
 * runs SCHED_FIFO, so an allocation that could take glibc's malloc arena lock, or fault in a page,
 * is a source of exactly the delay this component exists to avoid.
 *
 * So this is a bump pointer over one mmap'd, mlock'd, pre-faulted region, and freeing one
 * allocation is not supported. The region itself goes back when the last motion system holding it
 * does, which is what lets a process build one up and tear it down repeatedly. Exhaustion aborts:
 * the arena is sized for the worst case the rings can reach, so running out means the sizing is
 * wrong rather than that the machine is busy.
 */

#ifndef SRC_PLATFORM_MEMORYARENA_H_
#define SRC_PLATFORM_MEMORYARENA_H_

#include <Config/MachineLimits.h>

#include <new> // for std::align_val_t

namespace Duet::Sbc::MemoryArena
{
	// Reserve the arena, before anything allocates from it. Returns false if the region could not
	// be mapped; mlock failing is not fatal (it needs privileges the caller may not have) but is
	// reported through the log sink, because without it a page fault can stall a move.
	//
	// Reserving an arena that is already there hands out the same region and counts one more user
	// of it, so that a process holding two motion systems at once - which is the test bench
	// comparing one machine against another - shares one region and keeps it until both are done.
	// Like the rest of the create/destroy path, this is expected to be called from one thread.
	bool Reserve(size_t bytes) noexcept;

	// Give up one user's reservation, unmapping the region once the last of them has. Returns true
	// if that is what just happened, which is when the caller must also reset anything that
	// recycles objects out of the arena - see MoveSegment's free list. Everything allocated from it
	// dangles from that point.
	bool Release() noexcept;

	// Allocate. Never returns null: exhaustion aborts, for the reason in the header comment.
	void* Allocate(size_t count) noexcept;
	void* Allocate(size_t count, std::align_val_t align) noexcept;

	// Bytes still unallocated.
	[[nodiscard]] ptrdiff_t BytesFree() noexcept;
} // namespace Duet::Sbc::MemoryArena

#endif /* SRC_PLATFORM_MEMORYARENA_H_ */
