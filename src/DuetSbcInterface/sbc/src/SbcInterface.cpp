#include "DuetSbc/SbcInterface.h"

#include "DuetSbc/ProcessHelpers.h"

#include <chrono>
#include <cstring>

namespace duet::sbc {

namespace {
int64_t NowNs() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
               std::chrono::steady_clock::now().time_since_epoch())
        .count();
}
} // namespace

SbcInterface::SbcInterface(const Config &config) : _config(config), _transfer(config) {}

SbcInterface::~SbcInterface() { Stop(); }

void SbcInterface::Connect() { _transfer.Connect(); }

void SbcInterface::Start() {
    _stop.store(false, std::memory_order_relaxed);
    _thread = std::thread([this] { Execute(); });
}

void SbcInterface::Stop() {
    if (_stop.exchange(true)) {
        return;
    }
    _transfer.Stop();
    if (_thread.joinable()) {
        _thread.join();
    }
}

void SbcInterface::MarkRequest() {
    int64_t expected = 0;
    // Only the first request since the last completed transfer sets the timestamp
    _pendingRequestNs.compare_exchange_strong(expected, NowNs(), std::memory_order_relaxed);
}

void SbcInterface::RequestTransfer() {
    MarkRequest();
    _transfer.RequestTransfer();
}

void SbcInterface::QueueMessage(uint32_t messageFlags, std::string message) {
    {
        std::lock_guard<std::mutex> lock(_outgoingMutex);
        _messages.push(OutgoingMessage{messageFlags, std::move(message)});
    }
    RequestTransfer();
}

void SbcInterface::QueueCanMessage(uint16_t txToken, uint16_t msgType, uint16_t replyType, uint8_t dstAddress,
                                   bool isResponse, std::vector<uint8_t> payload) {
    {
        std::lock_guard<std::mutex> lock(_outgoingMutex);
        _canMessages.push(OutgoingCan{txToken, msgType, replyType, dstAddress, isResponse, std::move(payload)});
    }
    RequestTransfer();
}

void SbcInterface::QueueEnableCan(bool enable) {
    {
        std::lock_guard<std::mutex> lock(_outgoingMutex);
        _pendingEnableCan = true;
        _enableCanValue = enable;
    }
    RequestTransfer();
}

void SbcInterface::Execute() {
    // Pin and prioritise the transfer thread like LinkService does
    if (_config.isolateInterfaceThread && IsRaspberryPi()) {
        PinCurrentThreadToCore(_config.isolatedCoreId);
        if (_config.useRealtimeScheduling) {
            SetCurrentThreadRealtimePriority(_config.interfaceRtPriority);
        }
    }

    while (!_stop.load(std::memory_order_relaxed)) {
        // Invalidate on controller reset (just note it here)
        if (_transfer.HadReset()) {
            // Connection reset; nothing to invalidate in this test harness
        }

        // Process incoming packets from the previous transfer
        const int packets = _transfer.PacketsToRead();
        for (int i = 0; i < packets; i++) {
            proto::PacketHeader packet;
            if (!_transfer.ReadNextPacket(packet)) {
                break;
            }
            ProcessPacket(packet);
        }

        // Stage outgoing data and wait until there is a reason to perform another transfer
        do {
            StageOutgoing();
        } while (!_transfer.WaitForTransferReason());

        if (_stop.load(std::memory_order_relaxed)) {
            break;
        }

        // Do another full SPI transfer
        try {
            _transfer.PerformFullTransfer();
        } catch (const TransferTimeout &) {
            if (_stop.load(std::memory_order_relaxed)) {
                break;
            }
            // Lost connection is handled internally by reconnecting; loop again
            continue;
        }

        // Report jitter for a served request, if any
        const int64_t requestNs = _pendingRequestNs.exchange(0, std::memory_order_relaxed);
        if (requestNs != 0 && _onRequestServed) {
            _onRequestServed(NowNs() - requestNs);
        }
    }
}

void SbcInterface::StageOutgoing() {
    std::lock_guard<std::mutex> lock(_outgoingMutex);

    // CAN enable request
    if (_pendingEnableCan) {
        if (_transfer.WriteEnableCan(_enableCanValue)) {
            _pendingEnableCan = false;
        }
    }

    // Pending messages
    while (!_messages.empty()) {
        const OutgoingMessage &m = _messages.front();
        if (_transfer.WriteMessage(m.flags, m.text)) {
            _messages.pop();
        } else {
            break;
        }
    }

    // Pending CAN messages
    while (!_canMessages.empty()) {
        const OutgoingCan &c = _canMessages.front();
        if (_transfer.WriteCanMessage(c.txToken, c.msgType, c.replyType, c.dstAddress, c.isResponse,
                                      c.payload.data(), c.payload.size())) {
            _canMessages.pop();
        } else {
            break;
        }
    }
}

void SbcInterface::ProcessPacket(const proto::PacketHeader &packet) {
    const uint8_t *data = _transfer.PacketData();
    switch (static_cast<proto::FirmwareRequest>(packet.request)) {
        case proto::FirmwareRequest::ResendPacket: {
            proto::SbcRequest sbcRequest;
            _transfer.ResendPacket(packet, sbcRequest);
            break;
        }
        case proto::FirmwareRequest::CodeBufferUpdate: {
            proto::CodeBufferUpdateHeader header;
            std::memcpy(&header, data, sizeof(header));
            if (_onCodeBuffer) {
                _onCodeBuffer(header.bufferSpace);
            }
            break;
        }
        case proto::FirmwareRequest::Message: {
            proto::MessageHeader header;
            std::memcpy(&header, data, sizeof(header));
            std::string reply;
            if (header.length > 0) {
                reply.assign(reinterpret_cast<const char *>(data + sizeof(header)), header.length);
            }
            if (_onMessage) {
                _onMessage(header.messageType, reply);
            }
            break;
        }
        case proto::FirmwareRequest::MasterClock: {
            // Master clock is informational for this harness; ignore
            break;
        }
        case proto::FirmwareRequest::CANResponse: {
            proto::CanResponseHeader header;
            std::memcpy(&header, data, sizeof(header));
            if (_onCanResponse) {
                _onCanResponse(header, data + sizeof(header));
            }
            break;
        }
        case proto::FirmwareRequest::MotionStopped:
        default:
            break;
    }
}

} // namespace duet::sbc
