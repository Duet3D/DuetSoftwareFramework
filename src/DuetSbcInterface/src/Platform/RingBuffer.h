// Byte ring buffers carrying variable-length records across the managed/native boundary.
//
// These exist so the SPI interface thread never enters managed code. It runs pinned and SCHED_FIFO;
// if it invoked C# callbacks directly, every incoming message would run managed allocation, locks and
// potentially a GC pause on a real-time thread, mid-SPI-transfer. Instead the interface thread only
// touches these rings, and a normal-priority managed dispatcher thread drains them.
//
// Concurrency contract (deliberately asymmetric):
//   - There is exactly ONE consumer and it is lock-free. It only ever advances `_tail`.
//   - Producers may be many, but they serialise among THEMSELVES via `_producerMutex`. The consumer
//     never takes that mutex, so a producer holding it can never block the consumer -- which is what
//     keeps the real-time thread free of priority inversion when it is the consumer (outbound ring).
//
// Records are framed as [uint32 length][payload], 4-byte aligned. A record is never split across the
// end of the buffer: if it would not fit contiguously, a skip marker is written and the record starts
// at offset zero. That keeps the consumer's read path a single contiguous span with no wrap
// arithmetic, which is what lets the C ABI hand a pointer straight to C# without copying.
//
// `_head` and `_tail` are byte OFFSETS into the buffer, not monotonic counters. `_head == _tail`
// means empty; the writer always leaves at least one header's worth of slack so a full ring can
// never be mistaken for an empty one.
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <optional>
#include <span>
#include <type_traits>
#include <vector>

namespace Duet::Sbc
{

	// A run of bytes to write, or one that has been read back. Records are untyped on the way through
	// - the ring frames bytes and nothing more - so this is the currency of the whole interface.
	using ByteSpan = std::span<const uint8_t>;

	// View a trivially-copyable object as the bytes it occupies. Almost every record written here is
	// a packed command or event header followed by a payload, and this is what keeps the `sizeof` next
	// to the object it belongs to instead of in a parallel array of lengths.
	template <typename T>
	[[nodiscard]] ByteSpan AsBytes(const T& value) noexcept
	{
		static_assert(std::is_trivially_copyable_v<T>, "only trivially copyable objects can be written as bytes");
		// NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) - viewing an object as its bytes
		return {reinterpret_cast<const uint8_t *>(&value), sizeof(T)};
	}

	// The same for a pointer and a count that may legitimately be null/zero, which is how the optional
	// tail of an event arrives from callers that may not have one.
	[[nodiscard]] inline ByteSpan AsBytes(const void *data, size_t length) noexcept
	{
		return (data != nullptr && length > 0)
				   // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) - the ring is untyped
				   ? ByteSpan{static_cast<const uint8_t *>(data), length}
				   : ByteSpan{};
	}

	class RingBuffer
	{
	  public:
		// Length marker meaning "no more records before the end of the buffer; resume at offset 0"
		static constexpr uint32_t kSkipMarker = 0xFFFFFFFFu;
		static constexpr size_t kHeaderSize = sizeof(uint32_t);

		explicit RingBuffer(size_t capacity)
			: m_buffer(Align(capacity), 0)
			, capacity(Align(capacity))
		{
		}

		// --- Producer side (serialised by _producerMutex) ---

		// Append one record. Returns false if the ring is full, in which case nothing is written and the
		// caller must decide how to handle the overrun (the interface never blocks on a full ring).
		bool Write(ByteSpan data)
		{
			const ByteSpan fragments[1] = {data};
			const std::lock_guard<std::mutex> lock(m_producerMutex);
			return WriteLocked(fragments);
		}

		// Append one record assembled from several fragments, without an intermediate copy. This is the
		// common case: a small fixed-size event header followed by a variable-length payload.
		//
		// One span per fragment rather than an array of pointers beside an array of lengths: those two
		// had to be the same length and nothing said so, and each fragment's length had to be written
		// somewhere other than next to the thing it measured.
		bool WriteScattered(std::span<const ByteSpan> fragments)
		{
			const std::lock_guard<std::mutex> lock(m_producerMutex);
			return WriteLocked(fragments);
		}

		// --- Consumer side (single consumer, lock-free) ---

		// Peek at the next record without consuming it. Empty if the ring is empty. The bytes stay valid
		// until the next Consume() call.
		//
		// std::optional rather than an empty span for "nothing there": a zero-length record is a record,
		// and the caller that has to tell the two apart is the one draining the ring in a loop.
		std::optional<ByteSpan> Peek()
		{
			size_t tail = m_tail.load(std::memory_order_relaxed);
			size_t head = m_head.load(std::memory_order_acquire);
			if (tail == head)
			{
				m_pendingConsume = 0;
				return std::nullopt;
			}

			uint32_t recordLength = 0;
			std::memcpy(&recordLength, m_buffer.data() + tail, kHeaderSize);
			if (recordLength == kSkipMarker)
			{
				// Wrap to the start of the buffer. Publish the wrap before re-reading so the producer sees
				// the freed space at the end of the buffer.
				tail = 0;
				m_tail.store(tail, std::memory_order_release);
				head = m_head.load(std::memory_order_acquire);
				if (tail == head)
				{
					m_pendingConsume = 0;
					return std::nullopt;
				}
				std::memcpy(&recordLength, m_buffer.data() + tail, kHeaderSize);
			}

			m_pendingConsume = kHeaderSize + Align(recordLength);
			return ByteSpan{m_buffer.data() + tail + kHeaderSize, recordLength};
		}

		// Consume the record most recently returned by Peek().
		void Consume()
		{
			if (m_pendingConsume == 0)
			{
				return;
			}
			const size_t tail = m_tail.load(std::memory_order_relaxed);
			m_tail.store(tail + m_pendingConsume, std::memory_order_release);
			m_pendingConsume = 0;
		}

		[[nodiscard]] bool IsEmpty() const
		{
			return m_tail.load(std::memory_order_relaxed) == m_head.load(std::memory_order_acquire);
		}

		// Bytes not currently holding records. For deciding whether to offer more work, not whether
		// a particular write will fit: a record needs a contiguous run, and this counts the space
		// either side of a wrap together. It is a lower bound on what the reader will free, never an
		// upper bound on what the writer can use.
		[[nodiscard]] size_t BytesFree() const
		{
			const size_t head = m_head.load(std::memory_order_relaxed);
			const size_t tail = m_tail.load(std::memory_order_acquire);
			// head and tail are positions within the buffer rather than ever-increasing counters, so
			// a plain subtraction is only right while the ring has not wrapped.
			const size_t used = (head >= tail) ? head - tail : capacity - tail + head;
			return (used + kHeaderSize >= capacity) ? 0 : capacity - used - kHeaderSize;
		}

		// Number of records dropped because the ring was full (diagnostics).
		[[nodiscard]] uint64_t DroppedRecords() const { return m_dropped.load(std::memory_order_relaxed); }

	  private:
		// Records are kept 4-byte aligned so the length header is never read unaligned
		static constexpr size_t Align(size_t n) { return (n + 3u) & ~static_cast<size_t>(3u); }

		// Contiguous bytes writable at `head` before either catching the reader or hitting the end of the
		// buffer.
		//
		// One header's worth of slack is always reserved. That serves two purposes: `head` can never land
		// on `tail` (so head == tail unambiguously means empty), and `head` can never land exactly on
		// `_capacity` -- there is always room to write a skip marker at `head`. Without that second
		// guarantee a record ending flush with the end of the buffer would wedge the ring permanently:
		// no space to write, and no space for the marker that would let it wrap.
		[[nodiscard]] size_t FreeContiguous(size_t head, size_t tail) const
		{
			if (head >= tail)
			{
				// ...tail....head------end   (free to the end)
				return capacity - head - kHeaderSize;
			}
			// ...head....tail...   (free up to just before tail)
			return tail - head - kHeaderSize;
		}

		bool WriteLocked(std::span<const ByteSpan> fragments)
		{
			size_t payload = 0;
			for (const ByteSpan& fragment : fragments)
			{
				payload += fragment.size();
			}
			const size_t needed = kHeaderSize + Align(payload);
			if (needed + kHeaderSize > capacity)
			{
				// Larger than the ring can ever hold
				m_dropped.fetch_add(1, std::memory_order_relaxed);
				return false;
			}

			size_t head = m_head.load(std::memory_order_relaxed);
			const size_t tail = m_tail.load(std::memory_order_acquire);

			if (FreeContiguous(head, tail) < needed)
			{
				// Not enough contiguous room at the end. If the reader has moved past offset 0 we can
				// mark the remainder skippable and restart from the beginning.
				if (head >= tail && tail > kHeaderSize && (head + kHeaderSize) <= capacity)
				{
					const uint32_t marker = kSkipMarker;
					std::memcpy(m_buffer.data() + head, &marker, kHeaderSize);
					m_head.store(0, std::memory_order_release);
					head = 0;
					if (FreeContiguous(head, tail) < needed)
					{
						m_dropped.fetch_add(1, std::memory_order_relaxed);
						return false;
					}
				}
				else
				{
					m_dropped.fetch_add(1, std::memory_order_relaxed);
					return false;
				}
			}

			const auto recordLength = static_cast<uint32_t>(payload);
			std::memcpy(m_buffer.data() + head, &recordLength, kHeaderSize);
			size_t cursor = head + kHeaderSize;
			for (const ByteSpan& fragment : fragments)
			{
				if (!fragment.empty())
				{
					std::memcpy(m_buffer.data() + cursor, fragment.data(), fragment.size());
					cursor += fragment.size();
				}
			}

			// Publish the record only once all of its bytes are visible to the consumer
			m_head.store(head + needed, std::memory_order_release);
			return true;
		}

		std::vector<uint8_t> m_buffer;
		const size_t capacity;

		std::atomic<size_t> m_head{0};
		std::atomic<size_t> m_tail{0};

		std::mutex m_producerMutex;
		std::atomic<uint64_t> m_dropped{0};
		size_t m_pendingConsume = 0;
	};

} // namespace Duet::Sbc
