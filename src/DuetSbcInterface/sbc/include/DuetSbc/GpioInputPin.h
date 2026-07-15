// Reader for a single GPIO input line via the Linux GPIO character device.
// Prefers the v2 chardev uAPI (kernel 5.10+) for per-edge sequence numbers and falls back to the
// legacy v1 uAPI on older kernels. This talks to /dev/gpiochipN directly and needs no libgpiod.
//
// Unlike the original design there is no background monitor thread: the owning thread blocks directly
// on Fd() with poll()/ppoll() and drains edge events with ReadEvent(). That removes a thread wakeup
// hop from the latency path while still sleeping (0% CPU) when idle.
#pragma once

#include <cstdint>
#include <string>

namespace duet::sbc {

class GpioInputPin {
public:
    // Open a GPIO line for both-edge monitoring (non-blocking fd). Throws std::system_error on failure.
    GpioInputPin(const std::string &devNode, int line, const std::string &consumerLabel);
    ~GpioInputPin();

    GpioInputPin(const GpioInputPin &) = delete;
    GpioInputPin &operator=(const GpioInputPin &) = delete;

    // Event fd to pass to poll()/ppoll(). Readable when an edge event is queued.
    int Fd() const noexcept { return _reqFd; }

    // Read the current level of the line directly from the kernel (ioctl). Throws on failure.
    bool Read();

    // Consume one queued edge event (non-blocking). Updates the level and, on a rising edge, the
    // rising-edge sequence number. Returns true if an event was read, false if none were pending.
    // Throws std::system_error on a real read error.
    bool ReadEvent();

    // Sequence number of the most recently observed rising edge (kernel per-line seqno on v2, or a
    // running rising-edge count on v1).
    uint32_t RisingSequenceNumber() const noexcept { return _lastRisingSeqno; }

    // Number of edges the kernel dropped before they could be read (v2 event buffer overruns).
    int MissedEdges() const noexcept { return _missedEdges; }

    bool SupportsSequenceNumbers() const noexcept { return _useV2; }

private:
    bool TryRequestLineV2(const std::string &consumerLabel);
    void RequestLineV1(const std::string &consumerLabel);

    int _chipFd = -1;
    int _reqFd = -1;
    uint32_t _offset;
    bool _useV2 = false;

    uint32_t _lastSeqno = 0;       // seqno of the last edge (any direction), v2 only
    uint32_t _lastRisingSeqno = 0; // seqno of the last rising edge
    bool _haveSeqno = false;
    int _missedEdges = 0;
};

} // namespace duet::sbc
