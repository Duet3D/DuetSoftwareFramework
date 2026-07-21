// Standalone validation of duet::sbc::RingBuffer: framing, wrap/skip-marker, full-ring rejection,
// and a threaded producer/consumer soak that checks every record arrives intact and in order.
#include "DuetSbc/RingBuffer.h"

#include <atomic>
#include <cassert>
#include <cstdio>
#include <cstring>
#include <random>
#include <string>
#include <thread>
#include <vector>

using Duet::Sbc::RingBuffer;

static int failures = 0;
#define CHECK(cond, msg)                                                                                               \
	do                                                                                                                 \
	{                                                                                                                  \
		if (!(cond))                                                                                                   \
		{                                                                                                              \
			std::printf("FAIL: %s (line %d)\n", msg, __LINE__);                                                        \
			failures++;                                                                                                \
		}                                                                                                              \
	} while (0)

static bool ReadOne(RingBuffer& r, std::string& out)
{
	const uint8_t* data = nullptr;
	uint32_t len = 0;
	if (!r.Peek(data, len))
	{
		return false;
	}
	out.assign(reinterpret_cast<const char*>(data), len);
	r.Consume();
	return true;
}

static void TestBasicFraming()
{
	RingBuffer r(1024);
	std::string out;
	CHECK(!ReadOne(r, out), "empty ring must not yield a record");

	CHECK(r.Write("hello", 5), "write hello");
	CHECK(r.Write("world!!", 7), "write world");
	CHECK(ReadOne(r, out) && out == "hello", "first record round-trips");
	CHECK(ReadOne(r, out) && out == "world!!", "second record round-trips");
	CHECK(!ReadOne(r, out), "ring drained");
}

static void TestScattered()
{
	RingBuffer r(1024);
	uint32_t hdr = 0xDEADBEEF;
	const char* body = "payload";
	const void* frags[2] = {&hdr, body};
	const size_t lens[2] = {sizeof(hdr), 7};
	CHECK(r.WriteScattered(frags, lens, 2), "scattered write");

	const uint8_t* data = nullptr;
	uint32_t len = 0;
	CHECK(r.Peek(data, len), "peek scattered");
	CHECK(len == sizeof(hdr) + 7, "scattered length");
	uint32_t got = 0;
	std::memcpy(&got, data, sizeof(got));
	CHECK(got == 0xDEADBEEF, "scattered header intact");
	CHECK(std::memcmp(data + sizeof(hdr), body, 7) == 0, "scattered body intact");
	r.Consume();
}

// Force many wraps through a small ring, draining as we go.
static void TestWrapAround()
{
	RingBuffer r(128);
	std::string out;
	for (int i = 0; i < 5000; i++)
	{
		std::string payload(1 + (i % 40), static_cast<char>('a' + (i % 26)));
		CHECK(r.Write(payload.data(), payload.size()), "wrap write");
		CHECK(ReadOne(r, out), "wrap read");
		CHECK(out == payload, "wrap payload intact");
	}
}

// Interleave several outstanding records so head/tail chase each other around the wrap.
static void TestWrapWithBacklog()
{
	RingBuffer r(256);
	std::vector<std::string> pending;
	std::string out;
	std::mt19937 rng(1234);
	for (int i = 0; i < 20000; i++)
	{
		if ((rng() % 2) == 0)
		{
			std::string payload(1 + (rng() % 30), static_cast<char>('A' + (i % 26)));
			if (r.Write(payload.data(), payload.size()))
			{
				pending.push_back(payload);
			}
		}
		else if (!pending.empty())
		{
			CHECK(ReadOne(r, out), "backlog read");
			CHECK(out == pending.front(), "backlog order/content");
			pending.erase(pending.begin());
		}
	}
	while (!pending.empty())
	{
		CHECK(ReadOne(r, out), "drain read");
		CHECK(out == pending.front(), "drain order/content");
		pending.erase(pending.begin());
	}
	CHECK(!ReadOne(r, out), "fully drained");
}

static void TestFullRingRejects()
{
	RingBuffer r(64);
	int written = 0;
	while (r.Write("0123456789", 10))
	{
		written++;
		if (written > 100)
			break;
	}
	// `if (!(cond))`, not in this expression
	// NOLINTNEXTLINE(readability-simplify-boolean-expr) - the negation is inside CHECK's
	CHECK(written > 0 && written <= 100, "ring accepts then rejects");
	CHECK(r.DroppedRecords() > 0, "overrun counted");

	// After draining, writes must succeed again
	std::string out;
	while (ReadOne(r, out))
	{
	}
	CHECK(r.Write("0123456789", 10), "ring reusable after drain");

	// A record larger than the ring is always rejected
	std::string huge(1000, 'x');
	CHECK(!r.Write(huge.data(), huge.size()), "oversized record rejected");
}

// One producer, one consumer, checking every record arrives in order and intact.
static void TestThreadedSoak()
{
	RingBuffer r(4096);
	constexpr int kRecords = 200000;
	std::atomic<bool> done{false};
	std::atomic<int> produced{0};

	std::thread producer(
		[&]
		{
			std::mt19937 rng(99);
			for (int i = 0; i < kRecords;)
			{
				const size_t len = 4 + (rng() % 60);
				std::vector<uint8_t> payload(len);
				std::memcpy(payload.data(), &i, sizeof(i));
				for (size_t j = sizeof(i); j < len; j++)
				{
					payload[j] = static_cast<uint8_t>(i + j);
				}
				if (r.Write(payload.data(), payload.size()))
				{
					i++;
					produced.fetch_add(1);
				}
				else
				{
					std::this_thread::yield();
				}
			}
			done.store(true);
		});

	int expected = 0;
	while (expected < kRecords)
	{
		const uint8_t* data = nullptr;
		uint32_t len = 0;
		if (r.Peek(data, len))
		{
			int seq = 0;
			std::memcpy(&seq, data, sizeof(seq));
			if (seq != expected)
			{
				std::printf("FAIL: soak out of order, got %d expected %d\n", seq, expected);
				failures++;
				r.Consume();
				break;
			}
			bool bodyOk = true;
			for (uint32_t j = sizeof(seq); j < len; j++)
			{
				if (data[j] != static_cast<uint8_t>(seq + j))
				{
					bodyOk = false;
					break;
				}
			}
			if (!bodyOk)
			{
				std::printf("FAIL: soak payload corrupt at record %d\n", seq);
				failures++;
				r.Consume();
				break;
			}
			r.Consume();
			expected++;
		}
		else if (done.load() && r.IsEmpty())
		{
			break;
		}
	}
	producer.join();
	// The contract under contention: a full ring rejects the write (and counts it), the producer
	// retries, and nothing is lost. Rejections are expected here -- the producer deliberately outruns
	// the consumer -- so only delivery is asserted, not the absence of rejections.
	CHECK(expected == kRecords, "soak received every record");
}

int main()
{
	TestBasicFraming();
	TestScattered();
	TestWrapAround();
	TestWrapWithBacklog();
	TestFullRingRejects();
	TestThreadedSoak();
	if (failures == 0)
	{
		std::printf("All ring buffer tests passed.\n");
	}
	else
	{
		std::printf("%d check(s) failed.\n", failures);
	}
	return failures == 0 ? 0 : 1;
}
