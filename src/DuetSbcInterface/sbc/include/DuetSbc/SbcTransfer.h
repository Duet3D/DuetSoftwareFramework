// SBC-side SPI transfer engine: a faithful C++ port of
// DuetControlServer/Link/Adapter/SPI.cs (SPI transport only, no USB/IAP/firmware-update).
//
// It owns the TfrRdy/DataAvailable GPIO lines and the spidev device, drives the header/data/response
// exchange state machine against RepRapFirmware, and exposes packet read/write helpers plus the
// RequestTransfer / WaitForTransferReason gating used by the interface loop.
#pragma once

#include "DuetSbc/Config.h"
#include "DuetSbc/GpioInputPin.h"
#include "DuetSbc/OutputGpioPin.h"
#include "DuetSbc/SpiDevice.h"
#include "DuetSbcProtocol/MessageFormats.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace duet::sbc {

namespace proto = duet::sbc::protocol;

// Recoverable timeout/cancellation (maps to C# OperationCanceledException): the interface loop
// treats this as a lost connection and reconnects, unless a stop was requested.
class TransferTimeout : public std::runtime_error {
public:
    explicit TransferTimeout(const std::string &what) : std::runtime_error(what) {}
};

// Fatal protocol error (maps to a plain C# Exception): propagates out of the transfer loop.
class TransferError : public std::runtime_error {
public:
    explicit TransferError(const std::string &what) : std::runtime_error(what) {}
};

class SbcTransfer {
public:
    explicit SbcTransfer(const Config &config);
    ~SbcTransfer();

    SbcTransfer(const SbcTransfer &) = delete;
    SbcTransfer &operator=(const SbcTransfer &) = delete;

    // Optional callback used to report recovery/resync events (thread: interface thread).
    using LogCallback = std::function<void(const std::string &message)>;
    void SetLogCallback(LogCallback cb) { _logCallback = std::move(cb); }

    // Establish the initial connection (performs the first full transfer). Throws on failure.
    void Connect();

    // Perform a full data transfer synchronously. During normal operation this never throws for a
    // transfer error: it recovers internally by resynchronising with the controller (with backoff).
    // It only throws to unwind on Stop(), or from Connect() if the initial handshake fails.
    // `connecting` is true only for the very first one.
    void PerformFullTransfer(bool connecting = false);

    // Abandon the current connection and force a fresh handshake on the next transfer. Safe to call
    // from the interface loop after any unexpected error (e.g. while processing a malformed packet).
    void ResetConnection();

    // Number of times the connection has been resynchronised after an error (diagnostics).
    int ResyncCount() const noexcept { return _numResyncs; }

    int ProtocolVersion() const noexcept { return _protocolVersion; }

    // True if the controller has been reset (sequence number discontinuity).
    bool HadReset() const noexcept;

    // --- Transfer gating (see SPI.cs WaitForTransferReason / RequestTransfer) ---

    // Block while idle until there is a reason to start a full transfer. Returns true if a transfer
    // should start now, false if the caller should re-stage data and call again.
    bool WaitForTransferReason();

    // Notify the transfer loop that there is a reason to start a full transfer.
    void RequestTransfer();

    // --- Reading incoming packets ---
    int PacketsToRead() const noexcept { return _rxHeader.numPackets; }
    // Read the next packet header, or return false if none remain. Advances to the payload.
    bool ReadNextPacket(proto::PacketHeader &packet);
    // The payload of the packet most recently returned by ReadNextPacket.
    const uint8_t *PacketData() const noexcept { return _packetData; }
    uint16_t PacketDataLength() const noexcept { return _packetDataLength; }

    // --- Writing outgoing packets (return false if the buffer is full) ---
    bool WriteEmergencyStop();
    bool WriteReset();
    bool WriteEnableCan(bool enable);
    bool WriteCanMessage(uint16_t txToken, uint16_t msgType, uint16_t replyType, uint8_t dstAddress,
                         bool isResponse, const uint8_t *payload, size_t payloadLength);
    bool WriteMessage(uint32_t messageFlags, const std::string &message);

    // Resend a packet the firmware asked for. Throws TransferError if the id is unknown.
    void ResendPacket(const proto::PacketHeader &packet, proto::SbcRequest &sbcRequestOut);

    // Request cooperative shutdown of any in-progress wait.
    void Stop() noexcept;
    bool StopRequested() const noexcept { return _stop.load(std::memory_order_relaxed); }

    // --- Diagnostics ---
    double MaxFullTransferDelayMs() { double v = _maxFullTransferDelay; _maxFullTransferDelay = 0; return v; }
    double MaxPinWaitDurationMs() { double v = _maxPinWaitDuration; _maxPinWaitDuration = 0; return v; }
    int TfrPinGlitches() const noexcept { return _numTfrPinGlitches; }
    int MissedEdges() const noexcept { return _transferReadyPin->MissedEdges(); }

private:
    // State-machine steps (mirrors SPI.cs)
    void WaitForTransfer(bool inTransfer = true);
    void WriteCRC();
    bool ExchangeHeader();
    uint32_t ExchangeResponse(uint32_t response);
    bool ExchangeData();
    bool ExchangeDataResponse(bool &success);

    // Packet writing internals
    void WritePacketHeader(proto::SbcRequest request, size_t dataLength = 0);
    uint8_t *GetWriteBuffer(size_t dataLength);
    bool CanWritePacket(size_t dataLength = 0) const noexcept;

    void ThrowIfStopped();

    // Recovery: put the link back into the "reconnecting" state so the next transfer re-handshakes.
    void PrepareReconnect();
    // Sleep up to `ms`, returning early if Stop() is called (used to pace error retries).
    void InterruptibleSleep(int ms);

    const Config _config;
    const size_t _bufferSize;

    std::unique_ptr<GpioInputPin> _transferReadyPin;
    std::unique_ptr<GpioInputPin> _dataAvailablePin;
    // Optional scope trigger: high while data is staged, low once the transfer completes
    std::unique_ptr<OutputGpioPin> _sbcDataAvailablePin;

    // The rising-edge sequence number already consumed for the previous exchange. A sub-exchange must
    // wait for a rising edge newer than this rather than trusting a possibly stale high level.
    uint32_t _consumedRisingEdgeSeq = 0;

    // eventfds used to wake the interface thread out of poll(). The request fd is only watched between
    // transfers (WaitForTransferReason); the stop fd is watched everywhere so shutdown is prompt. Keeping
    // them separate means a RequestTransfer during a transfer does not spuriously wake the TfrRdy wait.
    int _requestEventFd = -1;
    int _stopEventFd = -1;

    std::unique_ptr<SpiDevice> _spiDevice;

    bool _waitingForFirstTransfer = true;
    bool _connected = false;
    bool _hadTimeout = false;
    bool _resetting = false;
    int _protocolVersion = 0;
    uint16_t _lastTransferNumber = 0;

    // Headers
    proto::SpiTransferHeader _rxHeader{};
    proto::SpiTransferHeader _txHeader{};
    uint8_t _packetId = 0;

    // Data buffers: three TX buffers so resend requests can be served
    static constexpr int kNumTxBuffers = 3;
    std::vector<std::vector<uint8_t>> _txBuffers;
    int _txBufferIndex = 0;
    std::vector<uint8_t> _rxBuffer;
    size_t _rxPointer = 0;
    size_t _txPointer = 0;

    // Most recently read packet payload
    proto::PacketHeader _lastPacket{};
    const uint8_t *_packetData = nullptr;
    uint16_t _packetDataLength = 0;

    // Requests currently being resent (avoid duplicates)
    std::vector<proto::SbcRequest> _packetsBeingResent;

    std::vector<uint8_t> &CurrentTxBuffer() { return _txBuffers[_txBufferIndex]; }

    std::atomic<bool> _stop{false};

    // Error recovery
    LogCallback _logCallback;
    int _consecutiveErrors = 0;
    int _numResyncs = 0;

    // Diagnostics
    std::chrono::steady_clock::time_point _keepAliveStart;
    std::chrono::steady_clock::time_point _fullTransferStart;
    bool _fullTransferTimerRunning = false;
    double _maxFullTransferDelay = 0;
    double _maxPinWaitDuration = 0;
    int _numTfrPinGlitches = 0;
    int _maxRxSize = 0;
    int _maxTxSize = 0;
};

} // namespace duet::sbc
