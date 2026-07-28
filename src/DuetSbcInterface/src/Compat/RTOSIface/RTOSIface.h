/*
 * RTOSIface.h - compatibility shim
 *
 * In the firmware these types shut out the step interrupt so that a reader sees a consistent view
 * of a drive's segment chain. There is no step interrupt here, so as written they have nothing to
 * do and are no-ops.
 *
 * That is only safe because of how the segment chains are read. They are touched exclusively by the
 * motion thread; the position snapshot that DuetControlServer reads through the C API is published
 * by that same thread into a seqlock-protected buffer, so no other thread ever walks a MoveSegment
 * list. If that ever stops being true, the fix is to move the reader onto the snapshot, not to give
 * these types a real lock - taking a mutex on the motion thread is what this component exists to
 * avoid.
 */

#ifndef SRC_COMPAT_RTOSIFACE_RTOSIFACE_H_
#define SRC_COMPAT_RTOSIFACE_RTOSIFACE_H_

#include <RepRapFirmware.h>

constexpr uint32_t NvicPriorityStep = 0;

class AtomicCriticalSectionLocker
{
public:
	AtomicCriticalSectionLocker() noexcept = default;
	~AtomicCriticalSectionLocker() = default;

	AtomicCriticalSectionLocker(const AtomicCriticalSectionLocker&) = delete;
	AtomicCriticalSectionLocker& operator=(const AtomicCriticalSectionLocker&) = delete;
};

class BasePriorityBooster
{
public:
	explicit BasePriorityBooster(uint32_t) noexcept { }
	~BasePriorityBooster() = default;

	BasePriorityBooster(const BasePriorityBooster&) = delete;
	BasePriorityBooster& operator=(const BasePriorityBooster&) = delete;
};

#endif /* SRC_COMPAT_RTOSIFACE_RTOSIFACE_H_ */
