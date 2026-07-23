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
#include <vector>

namespace Duet::Sbc
{

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
		bool Write(const void* data, size_t length)
		{
			const void* fragments[1] = {data};
			const size_t lengths[1] = {length};
			const std::lock_guard<std::mutex> lock(m_producerMutex);
			return WriteLocked(fragments, lengths, 1);
		}

		// Append one record assembled from several fragments, without an intermediate copy. This is the
		// common case: a small fixed-size event header followed by a variable-length payload.
		bool WriteScattered(const void* const* fragments, const size_t* lengths, size_t count)
		{
			const std::lock_guard<std::mutex> lock(m_producerMutex);
			return WriteLocked(fragments, lengths, count);
		}

		// --- Consumer side (single consumer, lock-free) ---

		// Peek at the next record without consuming it. Returns false if the ring is empty. The returned
		// pointer stays valid until the next Consume() call.
		bool Peek(const uint8_t*& data, uint32_t& length)
		{
			size_t tail = m_tail.load(std::memory_order_relaxed);
			size_t head = m_head.load(std::memory_order_acquire);
			if (tail == head)
			{
				m_pendingConsume = 0;
				return false;
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
					return false;
				}
				std::memcpy(&recordLength, m_buffer.data() + tail, kHeaderSize);
			}

			data = m_buffer.data() + tail + kHeaderSize;
			length = recordLength;
			m_pendingConsume = kHeaderSize + Align(recordLength);
			return true;
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

		bool WriteLocked(const void* const* fragments, const size_t* lengths, size_t count)
		{
			size_t payload = 0;
			for (size_t i = 0; i < count; i++)
			{
				payload += lengths[i];
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
			for (size_t i = 0; i < count; i++)
			{
				if (lengths[i] > 0)
				{
					std::memcpy(m_buffer.data() + cursor, fragments[i], lengths[i]);
					cursor += lengths[i];
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
