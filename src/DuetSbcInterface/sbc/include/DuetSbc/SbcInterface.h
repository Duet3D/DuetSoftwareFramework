// SBC-side communication loop: a trimmed C++ port of DuetControlServer/Link/LinkService.cs.
// Owns an SbcTransfer, runs the transfer loop on a pinned real-time thread, dispatches incoming
// packets and stages outgoing messages / CAN requests. Firmware update, IAP, file handling, IPC
// and object-model concerns are intentionally omitted; this exists to exercise the SPI transport.
#pragma once

#include "DuetSbc/Config.h"
#include "DuetSbc/SbcTransfer.h"
#include "DuetSbcProtocol/MessageFormats.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <queue>
#include <string>
#include <thread>
#include <vector>

namespace duet::sbc {

class SbcInterface {
public:
    // Called after each full transfer completes that served a request, with the measured latency
    // from RequestTransfer() to transfer completion (nanoseconds). This is the jitter metric.
    using RequestServedCallback = std::function<void(int64_t latencyNs)>;
    // Incoming firmware -> SBC notifications.
    using MessageCallback = std::function<void(uint32_t flags, const std::string &message)>;
    using CanResponseCallback = std::function<void(const proto::CanResponseHeader &header, const uint8_t *payload)>;
    using CodeBufferCallback = std::function<void(uint16_t bufferSpace)>;

    explicit SbcInterface(const Config &config);
    ~SbcInterface();

    // Connect to the firmware (blocks until the first transfer succeeds). Throws on failure.
    void Connect();

    // Start the transfer loop on its own pinned real-time thread.
    void Start();

    // Stop the transfer loop and join the thread.
    void Stop();

    // Queue an arbitrary message for transmission and request a transfer.
    void QueueMessage(uint32_t messageFlags, std::string message);
    // Queue a CAN message for transmission and request a transfer.
    void QueueCanMessage(uint16_t txToken, uint16_t msgType, uint16_t replyType, uint8_t dstAddress,
                         bool isResponse, std::vector<uint8_t> payload);
    // Queue a CAN enable/disable request and request a transfer.
    void QueueEnableCan(bool enable);

    // Force a transfer without new data (records the request timestamp for jitter measurement).
    void RequestTransfer();

    void SetRequestServedCallback(RequestServedCallback cb) { _onRequestServed = std::move(cb); }
    void SetMessageCallback(MessageCallback cb) { _onMessage = std::move(cb); }
    void SetCanResponseCallback(CanResponseCallback cb) { _onCanResponse = std::move(cb); }
    void SetCodeBufferCallback(CodeBufferCallback cb) { _onCodeBuffer = std::move(cb); }

    SbcTransfer &Transfer() noexcept { return _transfer; }

private:
    void Execute();
    void ProcessPacket(const proto::PacketHeader &packet);
    void StageOutgoing();
    void MarkRequest();

    struct OutgoingMessage {
        uint32_t flags;
        std::string text;
    };
    struct OutgoingCan {
        uint16_t txToken, msgType, replyType;
        uint8_t dstAddress;
        bool isResponse;
        std::vector<uint8_t> payload;
    };

    Config _config;
    SbcTransfer _transfer;
    std::thread _thread;
    std::atomic<bool> _stop{false};

    std::mutex _outgoingMutex;
    std::queue<OutgoingMessage> _messages;
    std::queue<OutgoingCan> _canMessages;
    bool _pendingEnableCan = false;
    bool _enableCanValue = false;

    // Jitter measurement: timestamp of the first RequestTransfer since the last completed transfer
    std::atomic<int64_t> _pendingRequestNs{0};

    RequestServedCallback _onRequestServed;
    MessageCallback _onMessage;
    CanResponseCallback _onCanResponse;
    CodeBufferCallback _onCodeBuffer;
};

} // namespace duet::sbc
