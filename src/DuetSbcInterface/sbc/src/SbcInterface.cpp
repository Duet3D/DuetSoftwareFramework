#include "DuetSbc/SbcInterface.h"

#include "DuetSbc/ProcessHelpers.h"

#include <poll.h>
#include <sys/eventfd.h>
#include <unistd.h>

#include <algorithm>
#include <chrono>
#include <cstring>

namespace duet::sbc {

namespace {

int64_t NowNs() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
               std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

// Ring capacities. The inbound ring must absorb a burst of incoming packets even if the managed
// dispatcher is briefly descheduled, so it is sized well beyond a single full transfer.
constexpr size_t kInboundCapacity = 256 * 1024;
constexpr size_t kOutboundCapacity = 128 * 1024;

} // namespace

SbcInterface::SbcInterface(const Config &config)
    : _config(config), _transfer(config), _inbound(kInboundCapacity), _outbound(kOutboundCapacity) {
    // Route the transfer engine's internal recovery reporting into the inbound ring
    _transfer.SetLogCallback([this](const std::string &message) {
        PostLog(LogLevel::Warning, message);
    });

    _inboundEventFd = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
}

SbcInterface::~SbcInterface() {
    Stop();
    if (_inboundEventFd >= 0) {
        ::close(_inboundEventFd);
        _inboundEventFd = -1;
    }
}

bool SbcInterface::WaitForInbound(int timeoutMs) {
    if (!_inbound.IsEmpty()) {
        return true;
    }
    if (_inboundEventFd < 0 || _stop.load(std::memory_order_relaxed)) {
        return !_inbound.IsEmpty();
    }

    // Announce that we are parking, then re-check: without this re-check an event posted between the
    // IsEmpty() above and the poll() below would leave us blocked until the timeout.
    _consumerWaiting.store(true, std::memory_order_seq_cst);
    if (!_inbound.IsEmpty()) {
        _consumerWaiting.store(false, std::memory_order_relaxed);
        return true;
    }

    pollfd pfd{_inboundEventFd, POLLIN, 0};
    ::poll(&pfd, 1, timeoutMs);
    _consumerWaiting.store(false, std::memory_order_relaxed);

    uint64_t value;
    while (::read(_inboundEventFd, &value, sizeof(value)) > 0) {
    }
    return !_inbound.IsEmpty();
}

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

    // Release a consumer parked in WaitForInbound so shutdown is prompt
    if (_inboundEventFd >= 0) {
        const uint64_t one = 1;
        [[maybe_unused]] ssize_t n = ::write(_inboundEventFd, &one, sizeof(one));
    }

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

// ---------------------------------------------------------------------------
// Outbound queueing (caller threads)
// ---------------------------------------------------------------------------
bool SbcInterface::QueueMessage(uint32_t messageFlags, const char *message, size_t length) {
    MessageCommand cmd{};
    cmd.header.type = static_cast<uint16_t>(OutboundCommandType::Message);
    cmd.flags = messageFlags;

    const void *fragments[2] = {&cmd, message};
    const size_t lengths[2] = {sizeof(cmd), message != nullptr ? length : 0};
    if (!_outbound.WriteScattered(fragments, lengths, 2)) {
        return false;
    }
    RequestTransfer();
    return true;
}

bool SbcInterface::QueueCanMessage(uint16_t txToken, uint16_t msgType, uint16_t replyType, uint8_t dstAddress,
                                   bool isResponse, const uint8_t *payload, size_t payloadLength) {
    CanMessageCommand cmd{};
    cmd.header.type = static_cast<uint16_t>(OutboundCommandType::CanMessage);
    cmd.txToken = txToken;
    cmd.msgType = msgType;
    cmd.replyType = replyType;
    cmd.dstAddress = dstAddress;
    cmd.isResponse = isResponse ? 1 : 0;

    const void *fragments[2] = {&cmd, payload};
    const size_t lengths[2] = {sizeof(cmd), payload != nullptr ? payloadLength : 0};
    if (!_outbound.WriteScattered(fragments, lengths, 2)) {
        return false;
    }
    RequestTransfer();
    return true;
}

bool SbcInterface::QueueEnableCan(bool enable, uint32_t requestId) {
    EnableCanCommand cmd{};
    cmd.header.type = static_cast<uint16_t>(OutboundCommandType::EnableCan);
    cmd.requestId = requestId;
    cmd.enable = enable ? 1 : 0;
    if (!_outbound.Write(&cmd, sizeof(cmd))) {
        return false;
    }
    RequestTransfer();
    return true;
}

void SbcInterface::RequestEmergencyStop(uint32_t requestId) {
    // Latched rather than queued: an e-stop must not be lost because the transfer buffer was full, and
    // must not queue up behind ordinary traffic
    _emergencyStopRequestId.store(requestId, std::memory_order_relaxed);
    _pendingEmergencyStop.store(true, std::memory_order_release);
    RequestTransfer();
}

void SbcInterface::RequestReset(uint32_t requestId) {
    _resetRequestId.store(requestId, std::memory_order_relaxed);
    _pendingReset.store(true, std::memory_order_release);
    RequestTransfer();
}

bool SbcInterface::RequestFirmwareUpdate(const uint8_t *iap, size_t iapLength, const uint8_t *firmware,
                                         size_t firmwareLength, uint16_t firmwareCrc16, uint32_t requestId) {
    if (iap == nullptr || firmware == nullptr || iapLength == 0 || firmwareLength == 0) {
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(_firmwareMutex);
        if (_pendingFirmwareUpdate.load(std::memory_order_acquire)) {
            return false;
        }
        _iapData = iap;
        _iapLength = iapLength;
        _firmwareData = firmware;
        _firmwareLength = firmwareLength;
        _firmwareCrc16 = firmwareCrc16;
        _firmwareRequestId = requestId;
        _pendingFirmwareUpdate.store(true, std::memory_order_release);
    }
    RequestTransfer();
    return true;
}

// ---------------------------------------------------------------------------
// Inbound event helpers (interface thread only)
// ---------------------------------------------------------------------------
void SbcInterface::PostEvent(InboundEventType type, const void *header, size_t headerLength,
                             const void *tail, size_t tailLength) {
    // The caller has already filled in the type; this just performs the scattered write
    (void)type;
    const void *fragments[2] = {header, tail};
    const size_t lengths[2] = {headerLength, tail != nullptr ? tailLength : 0};
    _inbound.WriteScattered(fragments, lengths, 2);

    // Wake a parked consumer. Skipped entirely while the dispatcher is keeping up, so the real-time
    // thread does not pay for a syscall on the hot path.
    if (_consumerWaiting.load(std::memory_order_seq_cst) && _inboundEventFd >= 0) {
        const uint64_t one = 1;
        [[maybe_unused]] ssize_t n = ::write(_inboundEventFd, &one, sizeof(one));
    }
}

void SbcInterface::PostLog(LogLevel level, const char *text, size_t length) {
    LogEvent event{};
    event.header.type = static_cast<uint16_t>(InboundEventType::Log);
    event.level = static_cast<uint8_t>(level);
    PostEvent(InboundEventType::Log, &event, sizeof(event), text, length);
}

void SbcInterface::CompleteRequest(uint32_t requestId, RequestResult result, const char *error,
                                   size_t errorLength) {
    if (requestId == kNoRequestId) {
        return;
    }
    RequestCompletedEvent event{};
    event.header.type = static_cast<uint16_t>(InboundEventType::RequestCompleted);
    event.requestId = requestId;
    event.result = static_cast<uint8_t>(result);
    PostEvent(InboundEventType::RequestCompleted, &event, sizeof(event), error, errorLength);
}

// ---------------------------------------------------------------------------
// The transfer loop (LinkService.cs Execute)
// ---------------------------------------------------------------------------
void SbcInterface::Execute() {
    // Pin and prioritise the transfer thread. This is the whole reason the loop lives in C++: it can
    // hold SCHED_FIFO on an isolated core without a managed runtime scheduling anything onto it.
    if (_config.isolateInterfaceThread && IsRaspberryPi()) {
        PinCurrentThreadToCore(_config.isolatedCoreId);
        if (_config.useRealtimeScheduling) {
            SetCurrentThreadRealtimePriority(_config.interfaceRtPriority);
        }
    }

    // The initial Connect() already completed a transfer, so report the link as up before looping
    if (_transfer.IsConnected() && !_wasConnected) {
        _wasConnected = true;
        ConnectionEstablishedEvent event{};
        event.header.type = static_cast<uint16_t>(InboundEventType::ConnectionEstablished);
        event.protocolVersion = static_cast<uint16_t>(_transfer.ProtocolVersion());
        PostEvent(InboundEventType::ConnectionEstablished, &event, sizeof(event));
    }

    while (!_stop.load(std::memory_order_relaxed)) {
        // The whole loop body is guarded so that any error -- a transfer failure the transfer engine
        // could not resolve, a malformed incoming packet, an I/O error -- results in an automatic
        // resync rather than terminating the thread.
        try {
            // A staged firmware update takes the loop over completely for its duration
            if (_pendingFirmwareUpdate.load(std::memory_order_acquire)) {
                PerformFirmwareUpdate();
                continue;
            }

            // Report a controller reset so the caller can invalidate its pending resources
            if (_transfer.HadReset()) {
                InboundEventHeader header{};
                header.type = static_cast<uint16_t>(InboundEventType::ControllerReset);
                PostEvent(InboundEventType::ControllerReset, &header, sizeof(header));
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

            // Stage outgoing data and wait until there is a reason to perform another transfer. Data
            // is (re-)staged before every decision so that data queued while idle is sent in the next
            // transfer without triggering an empty one either before or after it.
            do {
                StageOutgoing();
            } while (!_transfer.WaitForTransferReason());

            if (_stop.load(std::memory_order_relaxed)) {
                break;
            }

            // Do another full SPI transfer. This recovers from transfer errors internally and only
            // throws TransferTimeout to unwind on stop.
            _transfer.PerformFullTransfer();

            // Report connection state transitions
            const bool connected = _transfer.IsConnected();
            if (connected != _wasConnected) {
                _wasConnected = connected;
                if (connected) {
                    ConnectionEstablishedEvent event{};
                    event.header.type = static_cast<uint16_t>(InboundEventType::ConnectionEstablished);
                    event.protocolVersion = static_cast<uint16_t>(_transfer.ProtocolVersion());
                    PostEvent(InboundEventType::ConnectionEstablished, &event, sizeof(event));
                } else {
                    InboundEventHeader header{};
                    header.type = static_cast<uint16_t>(InboundEventType::ConnectionLost);
                    static constexpr char kReason[] = "Transfer timeout";
                    PostEvent(InboundEventType::ConnectionLost, &header, sizeof(header), kReason,
                              sizeof(kReason) - 1);
                }
            }

            // Report jitter for a served request, if any
            const int64_t requestNs = _pendingRequestNs.exchange(0, std::memory_order_relaxed);
            if (requestNs != 0 && _onRequestServed) {
                _onRequestServed(NowNs() - requestNs);
            }
        } catch (const TransferTimeout &) {
            if (_stop.load(std::memory_order_relaxed)) {
                break;
            }
            // Reconnection is handled inside PerformFullTransfer; just loop again
        } catch (const std::exception &e) {
            if (_stop.load(std::memory_order_relaxed)) {
                break;
            }
            const std::string message = std::string("Recovering from error in interface loop: ") + e.what();
            PostLog(LogLevel::Error, message);
            // Force a clean handshake on the next iteration
            _transfer.ResetConnection();
        }
    }
}

// ---------------------------------------------------------------------------
// Staging outgoing data (LinkService.cs, the do/while around WaitForTransferReason)
// ---------------------------------------------------------------------------
void SbcInterface::StageOutgoing() {
    // Emergency stop first: it is unconditional and invalidates everything else
    if (_pendingEmergencyStop.load(std::memory_order_acquire)) {
        if (_transfer.WriteEmergencyStop()) {
            _pendingEmergencyStop.store(false, std::memory_order_release);
            CompleteRequest(_emergencyStopRequestId.exchange(kNoRequestId, std::memory_order_relaxed),
                            RequestResult::Success);
            static constexpr char kMessage[] = "Emergency stop";
            PostLog(LogLevel::Warning, kMessage, sizeof(kMessage) - 1);
        }
        // An e-stop drops everything that was queued behind it
        return;
    }

    // Firmware reset: like the e-stop this clears the transfer buffer, so nothing else is staged
    if (_pendingReset.load(std::memory_order_acquire)) {
        if (_transfer.WriteReset()) {
            _pendingReset.store(false, std::memory_order_release);
            CompleteRequest(_resetRequestId.exchange(kNoRequestId, std::memory_order_relaxed),
                            RequestResult::Success);
            static constexpr char kMessage[] = "Resetting controller";
            PostLog(LogLevel::Warning, kMessage, sizeof(kMessage) - 1);
        }
        return;
    }

    // Drain queued commands until the transfer buffer is full. A command that does not fit is left in
    // the ring and retried on the next iteration, so ordering is preserved.
    const uint8_t *record;
    uint32_t length;
    while (_outbound.Peek(record, length)) {
        if (length < sizeof(OutboundCommandHeader)) {
            // Malformed record; drop it rather than wedging the ring
            _outbound.Consume();
            continue;
        }

        OutboundCommandHeader header;
        std::memcpy(&header, record, sizeof(header));
        const uint8_t *tail = record + sizeof(OutboundCommandHeader);
        const size_t tailLength = length - sizeof(OutboundCommandHeader);
        bool written = false;

        switch (static_cast<OutboundCommandType>(header.type)) {
            case OutboundCommandType::Message: {
                if (length < sizeof(MessageCommand)) {
                    _outbound.Consume();
                    continue;
                }
                MessageCommand cmd;
                std::memcpy(&cmd, record, sizeof(cmd));
                const char *text = reinterpret_cast<const char *>(record) + sizeof(MessageCommand);
                const size_t textLength = length - sizeof(MessageCommand);
                written = _transfer.WriteMessage(cmd.flags, std::string(text, textLength));
                break;
            }
            case OutboundCommandType::CanMessage: {
                if (length < sizeof(CanMessageCommand)) {
                    _outbound.Consume();
                    continue;
                }
                CanMessageCommand cmd;
                std::memcpy(&cmd, record, sizeof(cmd));
                const uint8_t *payload = record + sizeof(CanMessageCommand);
                const size_t payloadLength = length - sizeof(CanMessageCommand);
                written = _transfer.WriteCanMessage(cmd.txToken, cmd.msgType, cmd.replyType, cmd.dstAddress,
                                                    cmd.isResponse != 0, payload, payloadLength);
                break;
            }
            case OutboundCommandType::EnableCan: {
                if (length < sizeof(EnableCanCommand)) {
                    _outbound.Consume();
                    continue;
                }
                EnableCanCommand cmd;
                std::memcpy(&cmd, record, sizeof(cmd));
                written = _transfer.WriteEnableCan(cmd.enable != 0);
                if (written) {
                    CompleteRequest(cmd.requestId, RequestResult::Success);
                }
                break;
            }
            default:
                // Unknown command; drop it
                (void)tail;
                (void)tailLength;
                _outbound.Consume();
                continue;
        }

        if (!written) {
            // Transfer buffer is full: leave this command queued and try again next time
            break;
        }
        _outbound.Consume();
    }
}

// ---------------------------------------------------------------------------
// Incoming packets (LinkService.cs ProcessPacket)
// ---------------------------------------------------------------------------
void SbcInterface::ProcessPacket(const proto::PacketHeader &packet) {
    const uint8_t *data = _transfer.PacketData();
    const uint16_t dataLength = _transfer.PacketDataLength();

    switch (static_cast<proto::FirmwareRequest>(packet.request)) {
        case proto::FirmwareRequest::ResendPacket: {
            proto::SbcRequest sbcRequest;
            _transfer.ResendPacket(packet, sbcRequest);
            break;
        }
        case proto::FirmwareRequest::CodeBufferUpdate: {
            if (dataLength < sizeof(proto::CodeBufferUpdateHeader)) {
                break;
            }
            proto::CodeBufferUpdateHeader header;
            std::memcpy(&header, data, sizeof(header));

            CodeBufferEvent event{};
            event.header.type = static_cast<uint16_t>(InboundEventType::CodeBufferUpdate);
            event.bufferSpace = header.bufferSpace;
            PostEvent(InboundEventType::CodeBufferUpdate, &event, sizeof(event));
            break;
        }
        case proto::FirmwareRequest::Message: {
            if (dataLength < sizeof(proto::MessageHeader)) {
                break;
            }
            proto::MessageHeader header;
            std::memcpy(&header, data, sizeof(header));

            MessageEvent event{};
            event.header.type = static_cast<uint16_t>(InboundEventType::Message);
            event.flags = header.messageType;
            PostEvent(InboundEventType::Message, &event, sizeof(event), data + sizeof(header), header.length);
            break;
        }
        case proto::FirmwareRequest::MasterClock: {
            // Informational; DCS does not consume it
            break;
        }
        case proto::FirmwareRequest::CANResponse: {
            if (dataLength < sizeof(proto::CanResponseHeader)) {
                break;
            }
            proto::CanResponseHeader header;
            std::memcpy(&header, data, sizeof(header));

            CanResponseEvent event{};
            event.header.type = static_cast<uint16_t>(InboundEventType::CanResponse);
            event.txToken = header.txToken;
            event.msgType = header.msgType;
            event.dataLength = header.dataLength;
            event.srcAddress = header.srcAddress;
            event.flags = header.flags;
            event.status = header.status;
            PostEvent(InboundEventType::CanResponse, &event, sizeof(event), data + sizeof(header),
                      header.dataLength);
            break;
        }
        case proto::FirmwareRequest::MotionStopped:
            break;
        default: {
            // Unrecognised request: hand the raw bytes up so the caller can dump them for diagnostics
            MalformedPacketEvent event{};
            event.header.type = static_cast<uint16_t>(InboundEventType::MalformedPacket);
            event.packetId = packet.id;
            event.request = packet.request;
            event.length = packet.length;
            event.offset = static_cast<uint16_t>(_transfer.RxPointer());
            PostEvent(InboundEventType::MalformedPacket, &event, sizeof(event), data, dataLength);
            break;
        }
    }
}

// ---------------------------------------------------------------------------
// Firmware update (LinkService.cs PerformFirmwareUpdate)
// ---------------------------------------------------------------------------
void SbcInterface::PerformFirmwareUpdate() {
    const uint8_t *iap;
    size_t iapLength;
    const uint8_t *firmware;
    size_t firmwareLength;
    uint16_t crc16;
    uint32_t requestId;
    {
        std::lock_guard<std::mutex> lock(_firmwareMutex);
        iap = _iapData;
        iapLength = _iapLength;
        firmware = _firmwareData;
        firmwareLength = _firmwareLength;
        crc16 = _firmwareCrc16;
        requestId = _firmwareRequestId;
    }

    auto finish = [&](RequestResult result, const char *error) {
        {
            std::lock_guard<std::mutex> lock(_firmwareMutex);
            _iapData = _firmwareData = nullptr;
            _iapLength = _firmwareLength = 0;
            _firmwareRequestId = kNoRequestId;
            _pendingFirmwareUpdate.store(false, std::memory_order_release);
        }
        CompleteRequest(requestId, result, error, error != nullptr ? std::strlen(error) : 0);
    };

    try {
        // Send the IAP binary. Cancellation is safe at this stage.
        PostLog(LogLevel::Info, "Sending IAP binary", 18);
        for (size_t offset = 0; offset < iapLength;) {
            const size_t chunk = std::min(proto::IapSegmentSize, iapLength - offset);
            if (!_transfer.WriteIapSegment(iap + offset, chunk)) {
                break;
            }
            offset += chunk;
        }

        // Start the IAP binary. This is the point of no return: after this the board is running IAP
        // and the firmware transfer must complete or the board will need manual recovery.
        _transfer.StartIap();

        // From here on a stop request is not honoured for the data transfer -- interrupting a
        // flash-in-progress would brick the board. Only the retry boundary checks for shutdown.
        int numRetries = 0;
        bool verified = false;
        do {
            if (numRetries != 0) {
                if (_stop.load(std::memory_order_relaxed)) {
                    finish(RequestResult::Failed,
                           "Firmware update cancelled during retry. The board may need manual recovery.");
                    return;
                }
                PostLog(LogLevel::Error, "Firmware checksum verification failed", 36);
            }

            PostLog(LogLevel::Info, "Updating RepRapFirmware", 23);
            for (size_t offset = 0; offset < firmwareLength;) {
                const size_t chunk = std::min(proto::FirmwareSegmentSize, firmwareLength - offset);
                if (!_transfer.FlashFirmwareSegment(firmware + offset, chunk)) {
                    break;
                }
                offset += chunk;
            }

            PostLog(LogLevel::Info, "Verifying checksum", 18);
            verified = _transfer.VerifyFirmwareChecksum(static_cast<uint32_t>(firmwareLength), crc16);
        } while (!verified && ++numRetries < 3);

        if (!verified) {
            finish(RequestResult::Failed,
                   "Could not update firmware after 3 attempts. Please install it manually.");
            return;
        }

        // Wait for the IAP binary to restart the controller
        _transfer.WaitForIapReset();
        PostLog(LogLevel::Info, "Firmware update successful", 26);
        finish(RequestResult::Success, nullptr);
    } catch (const std::exception &e) {
        const std::string message = std::string("Failed to update firmware: ") + e.what();
        finish(RequestResult::Failed, message.c_str());
        // Force a clean handshake; the controller state is unknown after a failed flash
        _transfer.ResetConnection();
    }
}

} // namespace duet::sbc
