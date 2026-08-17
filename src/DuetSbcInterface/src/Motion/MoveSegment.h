/*
 * MoveSegment.h
 *
 *  Created on: 26 Feb 2021
 *      Author: David
 *
 * This class holds the parameters of a segment of a move with constant acceleration.
 * In order to handle input shaping we need to superimpose segments. This means we have to store the basic parameters.
 * The distance travelled when acceleration is a and initial speed is u is:
 *
 *		s = u*t + 0.5*a*t^2
 *
 * After n steps we want to achieve this distance plus any outstanding movement when the move started. So if q is the mm
 *per step then:
 *
 * 		n*q = s0 + u*t + 0.5*a*t^2
 *
 * The segment parameters are therefore s0, u and a. We also store the start time t0, the segment duration td, and the
 *segment length d. We don't need to store s0 in the segment, instead we accumulate it in the DriveMovement object. We
 *save memory by not storing u. If needed we calculate it from:
 *
 * 		u = (d - 0.5*a*td^2)/td = d/td - 0.5*a*td
 *
 * We can superimpose two segments that start at the same times t0 by adding the s0, u and a parameters.
 * If the segments start and/or end at different times then we must split one or both into two or three segments so that
 *we can superimpose segments with the same times.
 *
 * If S-curve acceleration is supported then a becomes the initial acceleration and we have an additional parameter j
 *which is the rate of change of acceleration. Distance travelled is:
 *
 * 		s = u*t + 0.5*a*t^2 + (1/6)*j*t^3
 *
 * Again, we don't store u because we can calculate it from:
 *
 * 		u = (d - 0.5*a*td^2 - (1/6)*j*td^3)/td = d/td - 0.5*a*td - (1/6)*j*td^2
 */

#ifndef SRC_MOTION_MOVESEGMENT_H_
#define SRC_MOTION_MOVESEGMENT_H_

#include <Config/MachineLimits.h>
#include <Platform/Log.h>
#include <Platform/MemoryArena.h>
#include <new> // for align_val_t

#ifndef SEGMENT_DEBUG
#  define SEGMENT_DEBUG (0)
#endif

constexpr motioncalc_t oneHalf = (motioncalc_t)0.5;

#if SUPPORT_S_CURVE
constexpr motioncalc_t OneSixth = (motioncalc_t)1.0 / (motioncalc_t)6.0;
constexpr motioncalc_t OneTwelfth = (motioncalc_t)1.0 / (motioncalc_t)12.0;
#  define J_FORMAL_PARAMETER(_name) , motioncalc_t _name
#  define J_ACTUAL_PARAMETER(_expr) , _expr
#else
#  define J_FORMAL_PARAMETER(_name)
#  define J_ACTUAL_PARAMETER(_name)
#endif

// This bit field is used in multiple contexts so that we can copy them efficiently from one context to another Not all
// flags are used in all contexts.
union MovementFlags final
{
	uint32_t all; // this is to provide a means to clear all the flags in one go
	struct
	{
		// The order of these flags matters, see function SameStaticFlags below. The first 4 flags do not change in a
		// segment.
		uint32_t nonPrintingMove : 1, // true if the move that generated this segment does not have both forwards
									  // extrusion and associated axis movement; used for filament monitoring
			checkEndstops : 1,		  // true if we need to check endstops or Z probe while executing this segment
			noShaping : 1,			  // true if input shaping should be disabled for this move
			isExtruder : 1,			  // true if this segment is for an extruder
									  // The remaining flags may change as a segment is processed
			executing : 1,			  // normally clear, set in a MoveSegment when the move starts to be executed
			combined : 1;			  // this is for debugging
	};

	constexpr void Clear() noexcept { all = 0; }

	constexpr void Init() noexcept
	{
		all = 0;
		nonPrintingMove = true;
	}

	// This operator sets checkingEndstops if either of the segments to be combined checks endstops, and sets
	// nonPrintingMove if either of them is a non printing move
	MovementFlags operator|(const MovementFlags other) const noexcept
	{
		MovementFlags ret{};
		ret.all = all | other.all;
		return ret;
	}

	MovementFlags& operator|=(const MovementFlags other) noexcept
	{
		all |= other.all;
		return *this;
	}

	[[nodiscard]] MovementFlags AddIsExtruder() const noexcept
	{
		MovementFlags ret{};
		ret.all = all;
		ret.isExtruder = true;
		return ret;
	}

	[[nodiscard]] bool SameStaticFlags(MovementFlags other) const noexcept
	{
		return (all & 0x0F) == (other.all & 0x0F);
	}

	bool operator==(MovementFlags other) const noexcept { return all == other.all; }
};

// This class stores the characteristics of a segment of a move with constant acceleration.
// The characteristics stored are the start time in step clocks, the duration in step clocks, the distance moved in
// steps, the acceleration, and some flags. We no longer store the initial speed because it can be calculated from the
// duration, distance and acceleration.
class MoveSegment final
{
  public:
	void* operator new(size_t count) noexcept { return Duet::Sbc::MemoryArena::Allocate(count); }
	void* operator new(size_t count, std::align_val_t align) noexcept
	{
		return Duet::Sbc::MemoryArena::Allocate(count, align);
	}
	void operator delete(void * /*ptr*/) noexcept {}
	void operator delete(void * /*ptr*/, std::align_val_t /*align*/) noexcept {}

	// Read the values of the flag bits
	[[nodiscard]] bool IsLinear() const noexcept { return m_a == (motioncalc_t)0.0; }
	[[nodiscard]] MovementFlags GetFlags() const noexcept { return m_flags; }

	// Given that this is not a constant-speed segment, test whether it is accelerating or decelerating
	[[nodiscard]] bool IsAccelerating() const noexcept { return m_a > (motioncalc_t)0.0; }

	// Get the segment start time in step clocks
	[[nodiscard]] uint32_t GetStartTime() const noexcept { return m_startTime; }

	// Get the segment duration in step clocks
	[[nodiscard]] uint32_t GetDuration() const noexcept { return m_duration; }

	// Get the initial speed
	[[nodiscard]] motioncalc_t CalcU() const noexcept;

	// Get the acceleration (the initial acceleration f we are supporting 3rd order motion control)
	[[nodiscard]] motioncalc_t GetA() const noexcept { return m_a; }

#if SUPPORT_S_CURVE
	// Get the rate of change of acceleration
	motioncalc_t GetJ() const noexcept { return m_j; }

	// Get the speed change
	motioncalc_t GetSpeedChange() const noexcept
	{
		return (m_a + m_j * (motioncalc_t)m_duration * oneHalf) * (motioncalc_t)m_duration;
	}

	// Get the acceleration change
	motioncalc_t GetAccChange() const noexcept { return m_j * (motioncalc_t)m_duration; }
#endif

	// Get the length
	[[nodiscard]] motioncalc_t GetLength() const noexcept { return m_distance; }

	// Set the parameters of this segment
	void SetParameters(uint32_t pStartTime,
					   uint32_t pDuration,
					   motioncalc_t pDistance,
					   motioncalc_t pA J_FORMAL_PARAMETER(p_j),
					   MovementFlags pFlags) noexcept;

	// Split this segment in two, returning a pointer to the second part
	MoveSegment* Split(uint32_t firstDuration) noexcept;

	// Merge the parameters for another segment with the same start time and duration into this one
	void Merge(motioncalc_t pDistance, motioncalc_t pA J_FORMAL_PARAMETER(p_j), MovementFlags pFlags) noexcept;

	// Set the 'executing' bit in the flags
	void SetExecuting() noexcept { m_flags.executing = true; }

	// Get the next segment in this list
	[[nodiscard]] MoveSegment* GetNext() const noexcept;

	// Set the next segment in this list
	void SetNext(MoveSegment* pNext) noexcept;

	// Combine the data from a previous short segment with this one. The previous segment must end at
	// the same time that this one begins, and must carry the same flags.
	void CombinePrevious(const MoveSegment* prev) noexcept;

	// Print this segment to the debug channel
	void DebugPrint() const noexcept;

	// Print list of segments
	static void DebugPrintList(const MoveSegment* segs) noexcept;

	// Allocate a MoveSegment, clearing the flags
	static MoveSegment* Allocate(MoveSegment* pNext) noexcept;

	// Release a MoveSegment
	static void Release(MoveSegment* item) noexcept;

	// Release all MoveSegments in a chain
	static void ReleaseAll(MoveSegment* item) noexcept;

	// Return the number of MoveSegment objects that have been created
	static unsigned int NumCreated() noexcept { return s_numCreated; }

  private:
	static MoveSegment* s_freeList;	  // list of recycled segment objects
	static unsigned int s_numCreated; // total number of segment objects created

	MoveSegment* m_next; // pointer to the next segment
	MovementFlags m_flags{};
	uint32_t m_startTime{};	   // when this segment should start, in movement clock ticks
	uint32_t m_duration{};	   // the duration of this segment in movement ticks
	motioncalc_t m_distance{}; // the number of steps moved
	motioncalc_t
		m_a{}; // the acceleration (initial if SUPPORT_S_CURVE) during this segment in steps per movement tick squared

#if SUPPORT_S_CURVE
	motioncalc_t m_j; // the jerk i.e. rate of change of acceleration
#endif

	explicit MoveSegment(MoveSegment* pNext) noexcept;
};

// Create a new one, leaving the flags clear
inline MoveSegment::MoveSegment(MoveSegment* pNext) noexcept
	: m_next(pNext)
{
	// remaining fields are not initialised
}

// Get the initial speed
inline motioncalc_t MoveSegment::CalcU() const noexcept
{
#if SUPPORT_S_CURVE
	return m_distance / (motioncalc_t)m_duration -
		   (oneHalf * m_a + OneSixth * m_j * (motioncalc_t)m_duration) * (motioncalc_t)m_duration;
#else
	return m_distance / (motioncalc_t)m_duration - oneHalf * m_a * (motioncalc_t)m_duration;
#endif
}

// Release a MoveSegment.
//
// The free list is not guarded, because it does not need to be: every allocation, release and
// traversal happens on the motion thread. Nothing may call into the segment machinery from another
// thread - the C API reads a position snapshot that thread publishes instead.
inline void MoveSegment::Release(MoveSegment* item) noexcept
{
	item->m_next = s_freeList;
	s_freeList = item;
}

inline MoveSegment* MoveSegment::GetNext() const noexcept
{
	return m_next;
}

inline void MoveSegment::SetNext(MoveSegment* pNext) noexcept
{
	m_next = pNext;
}

// Set the parameters of this segment
inline void MoveSegment::SetParameters(uint32_t pStartTime,
									   uint32_t pDuration,
									   motioncalc_t pDistance,
									   motioncalc_t pA J_FORMAL_PARAMETER(p_j),
									   MovementFlags pFlags) noexcept
{
	m_startTime = pStartTime;
	m_duration = pDuration;
	m_distance = pDistance;
	m_a = pA;
#if SUPPORT_S_CURVE
	m_j = p_j;
#endif
	m_flags = pFlags;
}

// Split this segment in two, returning a pointer to the new second part
inline MoveSegment* MoveSegment::Split(uint32_t firstDuration) noexcept
{
	MoveSegment* const secondSeg = Allocate(m_next);
#if SUPPORT_S_CURVE
	const motioncalc_t firstDistance =
		(CalcU() + (oneHalf * m_a + m_j * (motioncalc_t)firstDuration * OneSixth) * (motioncalc_t)firstDuration) *
		(motioncalc_t)firstDuration;
	secondSeg->SetParameters(m_startTime + firstDuration,
							 m_duration - firstDuration,
							 m_distance - firstDistance,
							 m_a + m_j * (motioncalc_t)firstDuration,
							 m_j,
							 m_flags);
#else
	const motioncalc_t firstDistance =
		(CalcU() + oneHalf * m_a * (motioncalc_t)firstDuration) * (motioncalc_t)firstDuration;
	secondSeg->SetParameters(
		m_startTime + firstDuration, m_duration - firstDuration, m_distance - firstDistance, m_a, m_flags);
#endif
#if SEGMENT_DEBUG
	DebugPrintf("split at %" PRIu32 ", fd=%.2f, sd=%.2f\n",
				firstDuration,
				(double)firstDistance,
				(double)(m_distance - firstDistance));
#endif
	m_duration = firstDuration;
	m_distance = firstDistance;
	m_next = secondSeg;
	return secondSeg;
}

// Merge the parameters for another segment with the same start time and duration into this one
// s = u*t * 0.5*a*t^2 therefore s1+s2 = (u1+u2)*t + 0.5*(a1+a2)*t^2
inline void MoveSegment::Merge(motioncalc_t pDistance,
							   motioncalc_t pA J_FORMAL_PARAMETER(p_j),
							   MovementFlags pFlags) noexcept
{
#if SEGMENT_DEBUG
	DebugPrintf("merge d=%.2f a=%.4e into ", (double)pDistance, (double)pA);
	DebugPrint();
#endif
	m_distance += pDistance;
	m_a += pA;
#if SUPPORT_S_CURVE
	m_j += p_j;
#endif
	m_flags |= pFlags;
}

// Combine the data from a previous short segment with this one. The previous segment ends at the same time that this
// one begins.
inline void MoveSegment::CombinePrevious(const MoveSegment* prev) noexcept
{
#if 0 // SUPPORT_S_CURVE
	const motioncalc_t finalAcc = m_a + m_j * (motioncalc_t)m_duration;
#endif
	m_duration += prev->m_duration;
	m_startTime = prev->m_startTime;
	m_distance += prev->m_distance;
#if 0 // SUPPORT_S_CURVE
	// Preserve the final acceleration of the segment. The segment that follows it may be temporarily detached, so don't use its starting acceleration.
	// However, this causes speed changes, so  it's disabled.
	m_a = prev->m_a;
	m_j = (finalAcc - m_a)/(motioncalc_t)m_duration;
#endif
	m_flags.combined = true;
}

#endif /* SRC_MOTION_MOVESEGMENT_H_ */
