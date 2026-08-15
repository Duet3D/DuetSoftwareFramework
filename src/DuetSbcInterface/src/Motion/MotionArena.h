/*
 * MotionArena.h
 *
 * The one region every long-lived motion object is allocated from.
 *
 * DDAs and MoveSegments are allocated once and never freed - see the operator new / no-op
 * operator delete pairs in Movement/DDA.h and Movement/MoveSegment.h. In RepRapFirmware that is a
 * memory-fragmentation measure as much as anything; here the reason is latency. The motion thread
 * runs SCHED_FIFO, so an allocation that could take glibc's malloc arena lock, or fault in a page,
 * is a source of exactly the delay this component exists to avoid.
 *
 * So this is a bump pointer over one mmap'd, mlock'd, pre-faulted region, and freeing is not
 * supported. Exhaustion aborts: the arena is sized for the worst case the rings can reach, so
 * running out means the sizing is wrong rather than that the machine is busy.
 */

#ifndef SRC_MOTION_MOTIONARENA_H_
#define SRC_MOTION_MOTIONARENA_H_

#include <RepRapFirmware.h>

#include <new>		// for std::align_val_t

namespace Duet::Sbc::Motion::MotionArena
{
	// Reserve the arena. Call once, before anything allocates from it. Returns false if the region
	// could not be mapped; mlock failing is not fatal (it needs privileges the caller may not have)
	// but is reported through the log sink, because without it a page fault can stall a move.
	bool Reserve(size_t bytes) noexcept;

	// Release the arena. Only for tests, which build up and tear down rings repeatedly; anything
	// allocated from it dangles afterwards.
	void Release() noexcept;

	// Allocate. Never returns null: exhaustion aborts, for the reason in the header comment.
	void *Allocate(size_t count) noexcept;
	void *Allocate(size_t count, std::align_val_t align) noexcept;

	// Bytes still unallocated.
	[[nodiscard]] ptrdiff_t BytesFree() noexcept;
}

#endif /* SRC_MOTION_MOTIONARENA_H_ */
