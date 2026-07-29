/*
 * Tasks.cpp - the permanent arena behind Tasks::AllocPermanent. See Compat/Platform/Tasks.h.
 */

#include <Platform/Tasks.h>

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <sys/mman.h>

namespace
{
	char *arenaBase = nullptr;
	size_t arenaSize = 0;
	size_t arenaUsed = 0;

	constexpr size_t defaultAlignment = alignof(std::max_align_t);

	size_t AlignUp(size_t value, size_t alignment) noexcept
	{
		return (value + alignment - 1) & ~(alignment - 1);
	}

	void *Allocate(size_t count, size_t alignment) noexcept
	{
		if (arenaBase == nullptr)
		{
			// Nothing reserved the arena. Every caller is a static-lifetime motion object, so this
			// is a startup ordering bug rather than something to paper over with a fallback malloc.
			std::fprintf(stderr, "duet_sbc: AllocPermanent called before InitPermanentArena\n");
			std::abort();
		}

		const size_t offset = AlignUp(arenaUsed, alignment);
		if (offset + count > arenaSize)
		{
			std::fprintf(stderr,
						 "duet_sbc: permanent arena exhausted (%zu bytes, wanted %zu more)\n",
						 arenaSize, count);
			std::abort();
		}

		arenaUsed = offset + count;
		return arenaBase + offset;
	}
}

bool Tasks::InitPermanentArena(size_t bytes) noexcept
{
	if (arenaBase != nullptr)
	{
		return true;						// already reserved
	}

	void * const mem = mmap(nullptr, bytes, PROT_READ | PROT_WRITE,
							MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
	if (mem == MAP_FAILED)
	{
		return false;
	}

	// Fault every page in now and pin them. A soft fault on the motion thread costs more than the
	// margin between preparing a move and its start time, and the whole arena is touched during a
	// print anyway, so there is nothing to gain by faulting lazily.
	std::memset(mem, 0, bytes);
	if (mlock(mem, bytes) != 0)
	{
		// Needs RLIMIT_MEMLOCK; a developer running the tests unprivileged will not have it. The
		// arena is still usable, just not pinned, so carry on rather than failing to start.
		DebugPrintf("could not mlock the motion arena (%zu bytes): timing may be affected\n", bytes);
	}

	arenaBase = static_cast<char *>(mem);
	arenaSize = bytes;
	arenaUsed = 0;
	return true;
}

void Tasks::ReleasePermanentArena() noexcept
{
	if (arenaBase != nullptr)
	{
		munmap(arenaBase, arenaSize);
		arenaBase = nullptr;
		arenaSize = 0;
		arenaUsed = 0;
	}
}

void *Tasks::AllocPermanent(size_t count) noexcept
{
	return Allocate(count, defaultAlignment);
}

void *Tasks::AllocPermanent(size_t count, std::align_val_t align) noexcept
{
	return Allocate(count, static_cast<size_t>(align));
}

ptrdiff_t Tasks::GetNeverUsedRam() noexcept
{
	return (ptrdiff_t)(arenaSize - arenaUsed);
}
