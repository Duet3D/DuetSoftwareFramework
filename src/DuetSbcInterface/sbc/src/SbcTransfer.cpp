#include "DuetSbc/SbcTransfer.h"

#include "DuetSbc/Crc.h"

#include <poll.h>
#include <sys/eventfd.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <system_error>

namespace duet::sbc {

namespace {

inline uint32_t ReadU32(const uint8_t *p) {
    uint32_t v;
    std::memcpy(&v, p, sizeof(v));
    return v;
}

inline uint16_t ReadU16(const uint8_t *p) {
    uint16_t v;
    std::memcpy(&v, p, sizeof(v));
    return v;
}

inline void WriteU16(uint8_t *p, uint16_t v) { std::memcpy(p, &v, sizeof(v)); }

using clock = std::chrono::steady_clock;

double ElapsedMs(clock::time_point start) {
    return std::chrono::duration<double, std::milli>(clock::now() - start).count();
}

} // namespace

SbcTransfer::SbcTransfer(const Config &config)
    : _config(config), _bufferSize(config.bufferSize) {
    _rxBuffer.assign(_bufferSize, 0);
    _txBuffers.resize(kNumTxBuffers);
    for (auto &buf : _txBuffers) {
        buf.assign(_bufferSize, 0);
    }

    // Initialize the TX header once (see Writer.InitTransferHeader)
    _txHeader.formatCode = proto::FormatCode;
    _txHeader.numPackets = 0;
    _txHeader.protocolVersion = proto::ProtocolVersion;
    _txHeader.sequenceNumber = 0;
    _txHeader.dataLength = 0;
    _txHeader.crcData = 0;
    _txHeader.crcHeader = 0;

    // GPIO lines. The interface thread waits on these fds directly with poll(); there is no separate
    // monitor thread, so a pin edge wakes the interface thread in a single hop.
    _dataAvailablePin = std::make_unique<GpioInputPin>(
        config.gpioChipDevice, config.dataAvailablePin, "sbc-dap-" + std::to_string(config.dataAvailablePin));
    _transferReadyPin = std::make_unique<GpioInputPin>(
        config.gpioChipDevice, config.transferReadyPin, "sbc-trp-" + std::to_string(config.transferReadyPin));

    // eventfds to wake the interface thread out of poll()
    _requestEventFd = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
    _stopEventFd = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
    if (_requestEventFd < 0 || _stopEventFd < 0) {
        throw std::system_error(errno, std::generic_category(), "Cannot create wake eventfd");
    }

    // Optional scope-trigger output line (low = idle)
    if (config.sbcDataAvailablePin >= 0) {
        _sbcDataAvailablePin = std::make_unique<OutputGpioPin>(
            config.gpioChipDevice, config.sbcDataAvailablePin,
            "sbc-sbcdap-" + std::to_string(config.sbcDataAvailablePin), false);
    }

    // Open the SPI device
    _spiDevice = std::make_unique<SpiDevice>(config.spiDevice, config.spiFrequency, config.spiTransferMode);

    _keepAliveStart = clock::now();
}

SbcTransfer::~SbcTransfer() {
    Stop();
    if (_requestEventFd >= 0) {
        ::close(_requestEventFd);
        _requestEventFd = -1;
    }
    if (_stopEventFd >= 0) {
        ::close(_stopEventFd);
        _stopEventFd = -1;
    }
}

void SbcTransfer::Stop() noexcept {
    _stop.store(true, std::memory_order_relaxed);
    // Wake the interface thread if it is blocked in poll() (anywhere)
    if (_stopEventFd >= 0) {
        const uint64_t one = 1;
        [[maybe_unused]] ssize_t n = ::write(_stopEventFd, &one, sizeof(one));
    }
}

void SbcTransfer::ThrowIfStopped() {
    if (_stop.load(std::memory_order_relaxed)) {
        throw TransferTimeout("Transfer cancelled");
    }
}

void SbcTransfer::Connect() {
    PerformFullTransfer(true);
}

bool SbcTransfer::HadReset() const noexcept {
    return _connected && (static_cast<uint16_t>(_lastTransferNumber + 1) != _rxHeader.sequenceNumber);
}

// ---------------------------------------------------------------------------
// Full transfer (SPI.cs PerformFullTransfer)
// ---------------------------------------------------------------------------
void SbcTransfer::PerformFullTransfer(bool connecting) {
    _packetsBeingResent.clear();
    _lastTransferNumber = _rxHeader.sequenceNumber;

    // Reset RX transfer header
    _rxHeader.formatCode = proto::InvalidFormatCode;
    _rxHeader.numPackets = 0;
    _rxHeader.protocolVersion = 0;
    _rxHeader.dataLength = 0;
    _rxHeader.crcData = 0;
    _rxHeader.crcHeader = 0;

    // Set up TX transfer header
    _txHeader.numPackets = _packetId;
    _txHeader.sequenceNumber++;
    _txHeader.dataLength = static_cast<uint16_t>(_txPointer);
    WriteCRC();

    int retry = 0;
    while (!_stop.load(std::memory_order_relaxed)) {
        try {
            if (retry > _config.maxSbcRetries) {
                throw TransferTimeout("Maximum number of SPI transfer retries exceeded");
            }

            // Track the maximum time between regular full transfers
            if (!connecting && !_waitingForFirstTransfer && _connected && !_hadTimeout && !_updating &&
                !_resetting) {
                if (_fullTransferTimerRunning) {
                    const double elapsed = ElapsedMs(_fullTransferStart);
                    if (elapsed > _maxFullTransferDelay) {
                        _maxFullTransferDelay = elapsed;
                    }
                    _fullTransferTimerRunning = false;
                } else {
                    _fullTransferStart = clock::now();
                    _fullTransferTimerRunning = true;
                }
            }

            // Exchange transfer headers (also deals with transfer responses)
            if (!ExchangeHeader()) {
                retry++;
                continue;
            }

            // Exchange data if there is anything to transfer
            if ((_rxHeader.dataLength != 0 || _txPointer != 0) && !ExchangeData()) {
                retry++;
                continue;
            }

            // Record the protocol version
            _protocolVersion = _rxHeader.protocolVersion;

            // Deal with timeouts and the first transmission
            if (_hadTimeout) {
                _hadTimeout = _resetting = false;
            } else if (!_connected) {
                _lastTransferNumber = static_cast<uint16_t>(_rxHeader.sequenceNumber - 1);
            }
            _connected = true;

            // Transfer OK
            if (_maxRxSize < _rxHeader.dataLength) _maxRxSize = _rxHeader.dataLength;
            if (_maxTxSize < _txHeader.dataLength) _maxTxSize = _txHeader.dataLength;
            _txBufferIndex = (_txBufferIndex + 1) % kNumTxBuffers;
            _rxPointer = _txPointer = 0;
            _packetId = 0;
            _keepAliveStart = clock::now();
            _consecutiveErrors = 0;

            // Transfer completed: drop the scope trigger low now that no data remains staged
            if (_sbcDataAvailablePin && _txPointer == 0) {
                _sbcDataAvailablePin->Write(false);
            }
            return;
        } catch (const TransferTimeout &) {
            // Timeout / cancellation. On stop or during the initial connect, unwind to the caller.
            if (connecting || _stop.load(std::memory_order_relaxed)) {
                throw;
            }
            if (!_hadTimeout && _connected && _logCallback) {
                _logCallback("Lost connection to controller (timeout); reconnecting");
            }
            // The pin wait already paced this, so just resync and keep retrying
            PrepareReconnect();
            retry = 0;
        } catch (const std::exception &e) {
            // Any other transfer error (bad format/checksum/protocol, SPI/GPIO I/O error, ...). During
            // the initial connect surface it; otherwise recover automatically rather than terminating.
            if (connecting || _stop.load(std::memory_order_relaxed)) {
                throw;
            }
            _numResyncs++;
            _consecutiveErrors++;
            if (_logCallback) {
                _logCallback("Transfer error, recovering (resync #" + std::to_string(_numResyncs) +
                             "): " + e.what());
            }
            PrepareReconnect();
            retry = 0;
            // Pace fast-failing errors (e.g. a persistent protocol mismatch) so recovery does not spin
            // the CPU; the backoff grows with consecutive failures up to a 1 s cap
            InterruptibleSleep(std::min(_consecutiveErrors * 50, 1000));
        }
    }

    // Stop requested
    throw TransferTimeout("Transfer cancelled");
}

// Put the link back into the "reconnecting" state so the next transfer re-runs the handshake. The
// pending TX data is preserved and retransmitted; only the connection/first-transfer flags are reset.
void SbcTransfer::PrepareReconnect() {
    _txHeader.protocolVersion = proto::ProtocolVersion;
    _waitingForFirstTransfer = true;
    if (!_hadTimeout && _connected) {
        _hadTimeout = true;
    }
    _connected = false;
    _resetting = false;

    // The header CRC may be stale after a partial/failed exchange or a protocol-version change
    WriteCRC();

    // Start the next handshake against a clean edge state (ignore errors while recovering)
    try {
        while (_transferReadyPin->ReadEvent()) {
        }
        _consumedRisingEdgeSeq = _transferReadyPin->RisingSequenceNumber();
        while (_dataAvailablePin->ReadEvent()) {
        }
    } catch (...) {
    }

    if (_sbcDataAvailablePin) {
        try {
            _sbcDataAvailablePin->Write(false);
        } catch (...) {
        }
    }
}

void SbcTransfer::ResetConnection() {
    // Abandon any partially-staged transfer and force a clean handshake on the next call
    _txPointer = 0;
    _rxPointer = 0;
    _packetId = 0;
    _packetsBeingResent.clear();
    _numResyncs++;
    PrepareReconnect();
}

void SbcTransfer::InterruptibleSleep(int ms) {
    if (ms <= 0 || _stop.load(std::memory_order_relaxed)) {
        return;
    }
    // Sleep on the stop fd so Stop() cuts the backoff short
    pollfd pfd{_stopEventFd, POLLIN, 0};
    ::poll(&pfd, 1, ms);
}

// ---------------------------------------------------------------------------
// Wait for the TfrRdy pin (SPI.cs WaitForTransfer)
// ---------------------------------------------------------------------------
void SbcTransfer::WaitForTransfer(bool inTransfer) {
    const bool needFreshEdge = inTransfer && !_waitingForFirstTransfer;

    // Sub-exchanges require a rising edge newer than the one consumed for the previous exchange;
    // the header and the first transfer run against the steady "ready" high level.
    auto isReady = [&]() -> bool {
        if (needFreshEdge) {
            const int32_t diff =
                static_cast<int32_t>(_transferReadyPin->RisingSequenceNumber() - _consumedRisingEdgeSeq);
            if (diff <= 0) {
                return false;
            }
            // A fresh rising edge occurred; confirm the pin is still high (else it was a glitch)
            if (_transferReadyPin->Read()) {
                return true;
            }
            _numTfrPinGlitches++;
            _consumedRisingEdgeSeq = _transferReadyPin->RisingSequenceNumber();
            return false;
        }
        return _transferReadyPin->Read();
    };

    // Drain any queued edge events (keeps the rising-edge seqno current and lets poll() block properly)
    while (_transferReadyPin->ReadEvent()) {
    }

    if (!isReady()) {
        // While IAP is running every wait uses IapTimeout: it erases flash between segments and can
        // leave TfrRdy low for far longer than any regular transfer timeout allows
        const int timeout =
            _updating ? proto::IapTimeout
                      : (_waitingForFirstTransfer
                             ? _config.sbcConnectTimeout
                             : (inTransfer ? _config.sbcTransferTimeout : _config.sbcConnectionTimeout));

        const auto start = clock::now();
        // Only the TfrRdy edge fd and the stop fd matter here; RequestTransfer wakeups are irrelevant
        // during a transfer and are intentionally not watched, so they cannot perturb this wait.
        pollfd fds[2];
        fds[0] = {_transferReadyPin->Fd(), POLLIN, 0};
        fds[1] = {_stopEventFd, POLLIN, 0};

        do {
            const int timeToWait = timeout - static_cast<int>(ElapsedMs(start));
            if (timeToWait <= 0 || _stop.load(std::memory_order_relaxed)) {
                if (_stop.load(std::memory_order_relaxed)) {
                    throw TransferTimeout("Transfer cancelled");
                }
                throw TransferTimeout(inTransfer ? "Transfer timeout while waiting for TfrRdy pin"
                                                 : "Connection timeout while waiting for TfrRdy pin");
            }

            // Block (0% CPU) until the TfrRdy pin toggles, a stop arrives, or the timeout elapses
            fds[0].revents = fds[1].revents = 0;
            const int ready = ::poll(fds, 2, timeToWait);
            if (ready < 0) {
                if (errno == EINTR) {
                    continue;
                }
                throw std::system_error(errno, std::generic_category(), "poll() failed waiting for TfrRdy");
            }

            // Drain the edge events that woke us and re-evaluate readiness (stop is handled at the top)
            while (_transferReadyPin->ReadEvent()) {
            }
        } while (!isReady());

        if (inTransfer) {
            const double waited = ElapsedMs(start);
            if (waited > _maxPinWaitDuration) {
                _maxPinWaitDuration = waited;
            }
        }
    }

    // Record the rising edge consumed for this exchange so the next sub-exchange waits for a newer one
    _consumedRisingEdgeSeq = _transferReadyPin->RisingSequenceNumber();
    _waitingForFirstTransfer = false;
}

// ---------------------------------------------------------------------------
// Checksums (SPI.cs WriteCRC)
// ---------------------------------------------------------------------------
void SbcTransfer::WriteCRC() {
    auto *hdr = reinterpret_cast<uint8_t *>(&_txHeader);
    const uint8_t *txData = CurrentTxBuffer().data();
    if (_txHeader.protocolVersion >= 4) {
        _txHeader.crcData = Crc32(txData, _txPointer);
        _txHeader.crcHeader = Crc32(hdr, 12);
    } else {
        const uint16_t data16 = Crc16(txData, _txPointer);
        WriteU16(hdr + 8, data16);
        const uint16_t header16 = Crc16(hdr, 10);
        WriteU16(hdr + 10, header16);
    }
}

// ---------------------------------------------------------------------------
// Header exchange (SPI.cs ExchangeHeader)
// ---------------------------------------------------------------------------
bool SbcTransfer::ExchangeHeader() {
    auto *txHdr = reinterpret_cast<uint8_t *>(&_txHeader);
    auto *rxHdr = reinterpret_cast<uint8_t *>(&_rxHeader);

    for (int retry = 0; retry < _config.maxSbcRetries; retry++) {
        // Perform SPI header exchange
        WaitForTransfer(false);
        const size_t headerLen = (_txHeader.protocolVersion >= 4) ? 16 : 12;
        _spiDevice->TransferFullDuplex(txHdr, rxHdr, headerLen);

        // Check for a possible response code. BadResponse always means "abandon this transfer and start
        // over", and DuetCANMaster only ever sends it on its way back to a header exchange. Restart the
        // full transfer so that both sides line up on a header. Answering with a data response instead
        // would pit our 4-byte response against its 16-byte header and oscillate: it would truncate the
        // header, reply BadHeaderChecksum, re-arm the header, and we would repeat
        const uint32_t responseCode = ReadU32(rxHdr);
        if (responseCode == proto::TransferResponse::BadResponse) {
            return false;
        }

        // Verify the format code. The protocol is little-endian, so a response code clocked out in place
        // of a header lands in formatCode and numPackets. No response code carries a valid format code in
        // its low byte, so this rejects a stray response before any other header field is trusted - in
        // particular the protocol version below, which is read before the header checksum can be verified
        if (_rxHeader.formatCode != proto::FormatCode &&
            _rxHeader.formatCode != proto::FormatCodeStandalone) {
            ExchangeResponse(proto::TransferResponse::BadResponse);
            return false;
        }

        // Change the protocol version if necessary
        // In update-only mode a newer-than-supported protocol version is adopted rather than refused,
        // so an incompatible firmware can still be flashed
        const uint16_t lastProtocolVersion = _txHeader.protocolVersion;
        if (_rxHeader.protocolVersion != lastProtocolVersion &&
            (_rxHeader.protocolVersion <= proto::ProtocolVersion || _config.updateOnly)) {
            _txHeader.protocolVersion = _rxHeader.protocolVersion;
            WriteCRC();
            ExchangeResponse(proto::TransferResponse::BadResponse);
            continue;
        }

        // Verify header checksum
        if (_rxHeader.protocolVersion >= 4) {
            const uint32_t crc32 = Crc32(rxHdr, 12);
            if (_rxHeader.crcHeader != crc32) {
                const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadHeaderChecksum);
                if (rc == proto::TransferResponse::BadResponse) {
                    // Both sides saw a bad header checksum: retry
                    return false;
                }
                continue;
            }
        } else {
            const uint16_t crc16 = Crc16(rxHdr, 10);
            if (ReadU16(rxHdr + 10) != crc16) {
                const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadHeaderChecksum);
                if (rc == proto::TransferResponse::BadResponse) {
                    return false;
                }
                continue;
            }
        }

        // Check format code. Any other value was already rejected before the checksum was verified
        if (_rxHeader.formatCode == proto::FormatCodeStandalone) {
            throw TransferError("RepRapFirmware is operating in stand-alone mode");
        }

        // Check for changed protocol version. Update-only mode tolerates a newer firmware so that it
        // can be reflashed rather than refused outright
        if (_rxHeader.protocolVersion > proto::ProtocolVersion && !_config.updateOnly) {
            ExchangeResponse(proto::TransferResponse::BadProtocolVersion);
            throw TransferError("Invalid protocol version");
        }

        // Check the data length
        if (_rxHeader.dataLength > _bufferSize) {
            ExchangeResponse(proto::TransferResponse::BadDataLength);
            throw TransferError("Data too long");
        }

        // Acknowledge receipt
        const uint32_t response = ExchangeResponse(proto::TransferResponse::Success);
        switch (response) {
            case proto::TransferResponse::Success:
                return true;
            case proto::TransferResponse::BadFormat:
                throw TransferError("RepRapFirmware refused message format");
            case proto::TransferResponse::BadProtocolVersion:
                throw TransferError("RepRapFirmware refused protocol version");
            case proto::TransferResponse::BadDataLength:
                throw TransferError("RepRapFirmware refused data length");
            case proto::TransferResponse::BadHeaderChecksum:
                continue;
            case proto::TransferResponse::BadResponse:
                return false;
            default:
                // Always announce the restart with BadResponse rather than quietly completing a transfer
                // whose header response the controller answered with something unexpected. DuetCANMaster
                // does the same from RestartTransfer(true), so both sides restart from a header exchange
                ExchangeResponse(proto::TransferResponse::BadResponse);
                return false;
        }
    }

    return false;
}

// ---------------------------------------------------------------------------
// Response exchange (SPI.cs ExchangeResponse)
// ---------------------------------------------------------------------------
uint32_t SbcTransfer::ExchangeResponse(uint32_t response) {
    uint32_t tx = response;
    uint32_t rx = 0;
    WaitForTransfer();
    _spiDevice->TransferFullDuplex(reinterpret_cast<const uint8_t *>(&tx), reinterpret_cast<uint8_t *>(&rx),
                                   sizeof(uint32_t));
    return rx;
}

// ---------------------------------------------------------------------------
// Data exchange (SPI.cs ExchangeData)
// ---------------------------------------------------------------------------
bool SbcTransfer::ExchangeData() {
    const size_t bytesToTransfer = std::max<size_t>(_rxHeader.dataLength, _txPointer);
    for (int retry = 0; retry < _config.maxSbcRetries; retry++) {
        WaitForTransfer();
        _spiDevice->TransferFullDuplex(CurrentTxBuffer().data(), _rxBuffer.data(), bytesToTransfer);

        // Check for a possible response code
        const uint32_t responseCode = ReadU32(_rxBuffer.data());
        if (responseCode == proto::TransferResponse::BadResponse) {
            return false;
        }

        // Inspect received data
        if (_rxHeader.protocolVersion >= 4) {
            const uint32_t crc32 = Crc32(_rxBuffer.data(), _rxHeader.dataLength);
            if (crc32 != _rxHeader.crcData) {
                const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadDataChecksum);
                if (rc == proto::TransferResponse::BadDataChecksum) {
                    // Both sides saw a bad data checksum: retry
                } else {
                    if (rc != proto::TransferResponse::BadResponse) {
                        ExchangeResponse(proto::TransferResponse::BadResponse);
                    }
                    return false;
                }
                continue;
            }
        } else {
            const uint16_t crc16 = Crc16(_rxBuffer.data(), _rxHeader.dataLength);
            const uint16_t expected = ReadU16(reinterpret_cast<uint8_t *>(&_rxHeader) + 8);
            if (crc16 != expected) {
                const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadDataChecksum);
                if (rc == proto::TransferResponse::BadResponse) {
                    return false;
                }
                continue;
            }
        }

        // Exchange data response and restart if it failed
        bool success = false;
        if (ExchangeDataResponse(success)) {
            return success;
        }
    }
    throw TransferError("SPI connection reset because the number of maximum retries has been exceeded");
}

// ---------------------------------------------------------------------------
// Data response exchange (SPI.cs ExchangeDataResponse)
// ---------------------------------------------------------------------------
bool SbcTransfer::ExchangeDataResponse(bool &success) {
    const uint32_t responseCode = ExchangeResponse(proto::TransferResponse::Success);
    switch (responseCode) {
        case proto::TransferResponse::Success:
            success = true;
            return true;
        case proto::TransferResponse::BadDataChecksum:
            success = false;
            return false;
        case proto::TransferResponse::BadResponse:
            success = false;
            return true;
        default:
            // Anything else means the two sides are no longer in step - typically DuetCANMaster has
            // already completed this transfer and moved on to a header, so it will never answer another
            // data response. Retrying here would oscillate against it forever. Send BadResponse, which
            // makes it restart from a header exchange too, and restart the full transfer
            ExchangeResponse(proto::TransferResponse::BadResponse);
            success = false;
            return true;
    }
}

// ---------------------------------------------------------------------------
// Transfer gating (SPI.cs WaitForTransferReason / RequestTransfer)
// ---------------------------------------------------------------------------
bool SbcTransfer::WaitForTransferReason() {
    // Only gate during normal operation; while connecting, reconnecting, resetting or updating the
    // protocol must always be free to make progress
    if (!_connected || _hadTimeout || _waitingForFirstTransfer || _updating || _resetting) {
        return true;
    }

    // Start straight away if we have data staged for transmission
    if (_txPointer != 0) {
        return true;
    }

    // Race-free DataAvailable check: drain queued edge events first, then read the authoritative level.
    // Draining stale events also stops them from making poll() return immediately below. A rising edge
    // that arrives after the drain is preserved (poll wakes on it) or is caught by the next Read().
    while (_dataAvailablePin->ReadEvent()) {
    }
    if (_dataAvailablePin->Read()) {
        return true;
    }

    // Keep-alive
    const int timeToWait =
        _config.sbcConnectionKeepAliveInterval - static_cast<int>(ElapsedMs(_keepAliveStart));
    if (timeToWait <= 0) {
        return true;
    }

    // Block (0% CPU) until a reason arrives: a wake-up from RequestTransfer/Stop, the DataAvailable
    // pin rising, or the keep-alive timeout.
    //
    // The wake eventfd is deliberately NOT drained before poll(). StageOutgoing() (which reads the
    // outgoing queue) has already run in the caller's loop; a RequestTransfer that races in *after*
    // that read but before/inside this poll() must still wake us, otherwise its message would sit in
    // the queue until the keep-alive fires (a spurious ~25 ms gap). Draining only after poll() keeps
    // the signal edge-safe. A leftover count from RequestTransfers served by data-driven transfers
    // merely causes one immediate, harmless extra wake-up before we settle.
    pollfd fds[3];
    fds[0] = {_dataAvailablePin->Fd(), POLLIN, 0};
    fds[1] = {_requestEventFd, POLLIN, 0};
    fds[2] = {_stopEventFd, POLLIN, 0};
    const int ready = ::poll(fds, 3, timeToWait);
    if (ready < 0 && errno != EINTR) {
        throw std::system_error(errno, std::generic_category(), "poll() failed waiting for a transfer reason");
    }

    // Consume the wake-ups and any DataAvailable edges so the next poll() blocks properly
    uint64_t v;
    while (::read(_requestEventFd, &v, sizeof(v)) > 0) {
    }
    while (::read(_stopEventFd, &v, sizeof(v)) > 0) {
    }
    while (_dataAvailablePin->ReadEvent()) {
    }

    // Proceed on stop so the caller can shut down; otherwise re-stage and retry (the next call
    // re-checks the DataAvailable level / staged data / keep-alive and starts a transfer if warranted)
    return _stop.load(std::memory_order_relaxed);
}

void SbcTransfer::RequestTransfer() {
    // Raise the scope trigger: the SBC now has a reason (typically staged data) to transfer
    if (_sbcDataAvailablePin) {
        _sbcDataAvailablePin->Write(true);
    }
    // Wake the interface thread if it is blocked in WaitForTransferReason's poll()
    if (_requestEventFd >= 0) {
        const uint64_t one = 1;
        [[maybe_unused]] ssize_t n = ::write(_requestEventFd, &one, sizeof(one));
    }
}

// ---------------------------------------------------------------------------
// Reading incoming packets (SPI.cs ReadNextPacket)
// ---------------------------------------------------------------------------
bool SbcTransfer::ReadNextPacket(proto::PacketHeader &packet) {
    if (_rxPointer >= _rxHeader.dataLength) {
        return false;
    }

    std::memcpy(&_lastPacket, _rxBuffer.data() + _rxPointer, sizeof(proto::PacketHeader));
    _rxPointer += sizeof(proto::PacketHeader);

    _packetData = _rxBuffer.data() + _rxPointer;
    _packetDataLength = _lastPacket.length;
    _rxPointer += proto::AddPadding(_lastPacket.length);

    packet = _lastPacket;
    return true;
}

// ---------------------------------------------------------------------------
// Writing outgoing packets (SPI.cs Write* helpers)
// ---------------------------------------------------------------------------
bool SbcTransfer::CanWritePacket(size_t dataLength) const noexcept {
    return _txPointer + sizeof(proto::PacketHeader) + dataLength <= _bufferSize;
}

void SbcTransfer::WritePacketHeader(proto::SbcRequest request, size_t dataLength) {
    proto::PacketHeader header{};
    header.request = static_cast<uint16_t>(request);
    header.id = _packetId++;
    header.length = static_cast<uint16_t>(dataLength);
    header.resendPacketId = 0;
    std::memcpy(CurrentTxBuffer().data() + _txPointer, &header, sizeof(header));
    _txPointer += sizeof(header);
}

uint8_t *SbcTransfer::GetWriteBuffer(size_t dataLength) {
    const size_t padded = proto::AddPadding(dataLength);
    uint8_t *result = CurrentTxBuffer().data() + _txPointer;
    // Zero any padding bytes
    if (padded > dataLength) {
        std::memset(result + dataLength, 0, padded - dataLength);
    }
    _txPointer += padded;
    return result;
}

bool SbcTransfer::WriteEmergencyStop() {
    if (!CanWritePacket()) {
        return false;
    }
    WritePacketHeader(proto::SbcRequest::EmergencyStop);
    return true;
}

bool SbcTransfer::WriteReset() {
    if (!CanWritePacket()) {
        return false;
    }
    _txPointer = 0;
    _resetting = true;
    WritePacketHeader(proto::SbcRequest::Reset);
    return true;
}

bool SbcTransfer::WriteEnableCan(bool enable) {
    if (!CanWritePacket(sizeof(proto::EnableCanHeader))) {
        return false;
    }
    WritePacketHeader(proto::SbcRequest::EnableCAN, sizeof(proto::EnableCanHeader));
    proto::EnableCanHeader header{};
    header.channel = 0;
    header.enable = enable ? 1 : 0;
    std::memcpy(GetWriteBuffer(sizeof(header)), &header, sizeof(header));
    return true;
}

bool SbcTransfer::WriteCanMessage(uint16_t txToken, uint16_t msgType, uint16_t replyType, uint8_t dstAddress,
                                  bool isResponse, const uint8_t *payload, size_t payloadLength) {
    if (payloadLength > 64) {
        throw TransferError("CAN message payload must be between 0 and 64 bytes");
    }
    const size_t dataLength = sizeof(proto::SendCanMessageHeader) + payloadLength;
    if (!CanWritePacket(proto::AddPadding(dataLength))) {
        return false;
    }

    WritePacketHeader(proto::SbcRequest::SendCANMessage, proto::AddPadding(dataLength));

    proto::SendCanMessageHeader header{};
    header.txToken = txToken;
    header.msgType = msgType;
    header.replyType = replyType;
    header.dataLength = static_cast<uint8_t>(payloadLength);
    header.dstAddress = dstAddress;
    header.flags = isResponse ? 0x01 : 0x00;

    uint8_t *dst = GetWriteBuffer(dataLength);
    std::memcpy(dst, &header, sizeof(header));
    if (payloadLength > 0) {
        std::memcpy(dst + sizeof(header), payload, payloadLength);
    }
    return true;
}

bool SbcTransfer::WriteMessage(uint32_t messageFlags, const std::string &message) {
    // Don't send a new request if another one is still pending
    if (std::find(_packetsBeingResent.begin(), _packetsBeingResent.end(), proto::SbcRequest::Message) !=
        _packetsBeingResent.end()) {
        return false;
    }

    const size_t dataLength = sizeof(proto::MessageHeader) + message.size();
    if (!CanWritePacket(proto::AddPadding(dataLength))) {
        return false;
    }

    WritePacketHeader(proto::SbcRequest::Message, proto::AddPadding(dataLength));

    proto::MessageHeader header{};
    header.messageType = messageFlags;
    header.length = static_cast<uint16_t>(message.size());
    header.padding = 0;

    uint8_t *dst = GetWriteBuffer(dataLength);
    std::memcpy(dst, &header, sizeof(header));
    if (!message.empty()) {
        std::memcpy(dst + sizeof(header), message.data(), message.size());
    }
    return true;
}

// ---------------------------------------------------------------------------
// Resend a packet (SPI.cs ResendPacket)
// ---------------------------------------------------------------------------
void SbcTransfer::ResendPacket(const proto::PacketHeader &packet, proto::SbcRequest &sbcRequestOut) {
    // The packet to resend lives in the previously-used TX buffer
    const int prevIndex = (_txBufferIndex + 1) % kNumTxBuffers;
    const uint8_t *buffer = _txBuffers[prevIndex].data();
    const size_t headerSize = sizeof(proto::PacketHeader);
    size_t offset = 0;

    for (;;) {
        proto::PacketHeader header;
        std::memcpy(&header, buffer + offset, headerSize);
        if (header.id == packet.resendPacketId) {
            sbcRequestOut = static_cast<proto::SbcRequest>(header.request);
            WritePacketHeader(sbcRequestOut, header.length);
            std::memcpy(GetWriteBuffer(header.length), buffer + offset + headerSize, header.length);

            if (std::find(_packetsBeingResent.begin(), _packetsBeingResent.end(), sbcRequestOut) ==
                _packetsBeingResent.end()) {
                _packetsBeingResent.push_back(sbcRequestOut);
            }
            return;
        }

        offset += headerSize + proto::AddPadding(header.length);
        if (header.id >= packet.resendPacketId || offset >= _bufferSize) {
            break;
        }
    }

    throw TransferError("Firmware requested resend for invalid packet");
}

// ---------------------------------------------------------------------------
// IAP / firmware update (SPI.cs WriteIapSegment .. WaitForIapReset)
// ---------------------------------------------------------------------------
bool SbcTransfer::WriteIapSegment(const uint8_t *data, size_t length) {
    if (data == nullptr || length == 0) {
        return false;
    }
    if (length > proto::IapSegmentSize) {
        throw TransferError("IAP segment too large");
    }

    WritePacketHeader(proto::SbcRequest::WriteIap, length);
    std::memcpy(GetWriteBuffer(length), data, length);
    PerformFullTransfer();
    return true;
}

void SbcTransfer::StartIap() {
    // Tell the firmware to boot the IAP program
    WritePacketHeader(proto::SbcRequest::StartIap);
    PerformFullTransfer();

    // From here on the controller runs IAP, which speaks a much simpler protocol: bare full-duplex
    // transfers gated by TfrRdy only. It raises the pin once it is ready to receive the firmware.
    _waitingForFirstTransfer = _updating = true;
}

bool SbcTransfer::FlashFirmwareSegment(const uint8_t *data, size_t length) {
    if (data == nullptr || length == 0) {
        return false;
    }
    if (length > proto::FirmwareSegmentSize) {
        throw TransferError("Firmware segment too large");
    }

    uint8_t writeBuffer[proto::FirmwareSegmentSize];
    uint8_t readBuffer[proto::FirmwareSegmentSize];
    std::memcpy(writeBuffer, data, length);
    if (length < proto::FirmwareSegmentSize) {
        // Fill the remaining space with 0xFF, as the IAP program does once complete
        std::memset(writeBuffer + length, 0xFF, proto::FirmwareSegmentSize - length);
    }

    WaitForTransfer();
    _spiDevice->TransferFullDuplex(writeBuffer, readBuffer, proto::FirmwareSegmentSize);
    return true;
}

bool SbcTransfer::VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16) {
    // At this point IAP expects another segment, so wait for it to be ready first. After that give it
    // a moment to acknowledge that we are done before sending the verification request.
    WaitForTransfer();
    InterruptibleSleep(proto::FirmwareFinishedDelay);

    // Send the final firmware size plus CRC16 checksum to IAP
    proto::FlashVerify verifyRequest{};
    verifyRequest.firmwareLength = firmwareLength;
    verifyRequest.crc16 = crc16;
    verifyRequest.padding = 0;

    uint8_t transferData[sizeof(proto::FlashVerify)];
    std::memcpy(transferData, &verifyRequest, sizeof(verifyRequest));
    WaitForTransfer();
    _spiDevice->TransferFullDuplex(transferData, transferData, sizeof(transferData));

    // Check whether IAP can confirm our CRC16 checksum
    uint8_t writeOk[1] = {0};
    WaitForTransfer();
    _spiDevice->TransferFullDuplex(writeOk, writeOk, sizeof(writeOk));
    return writeOk[0] == proto::FlashVerifyOk;
}

void SbcTransfer::WaitForIapReset() {
    // Wait a moment for the firmware to start
    InterruptibleSleep(proto::IapRebootDelay);

    // Wait for the first data transfer from the newly-started firmware. Seeding the sequence numbers
    // this way makes the next exchange look like a fresh connection rather than a reset.
    _updating = _connected = false;
    _waitingForFirstTransfer = true;
    _rxHeader.sequenceNumber = 1;
    _txHeader.sequenceNumber = 0;
}

} // namespace duet::sbc
