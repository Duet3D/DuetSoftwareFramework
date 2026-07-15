#include "DuetSbc/CApi.h"

#include "DuetSbc/Config.h"
#include "DuetSbc/SbcInterface.h"

#include <cstring>
#include <exception>
#include <string>

using duet::sbc::Config;
using duet::sbc::SbcInterface;

struct DuetSbcHandle {
    Config config;
    SbcInterface interface;
    explicit DuetSbcHandle(const Config &cfg) : config(cfg), interface(cfg) {}
};

namespace {

void CopyError(char *buf, int32_t len, const std::string &msg) {
    if (buf != nullptr && len > 0) {
        const size_t n = std::min<size_t>(msg.size(), static_cast<size_t>(len - 1));
        std::memcpy(buf, msg.data(), n);
        buf[n] = '\0';
    }
}

Config FromC(const DuetSbcConfig *c) {
    Config cfg;
    if (c == nullptr) {
        return cfg;
    }
    if (c->spiDevice) cfg.spiDevice = c->spiDevice;
    if (c->spiFrequency) cfg.spiFrequency = c->spiFrequency;
    cfg.spiTransferMode = c->spiTransferMode;
    if (c->bufferSize > 0) cfg.bufferSize = static_cast<size_t>(c->bufferSize);
    if (c->gpioChipDevice) cfg.gpioChipDevice = c->gpioChipDevice;
    cfg.transferReadyPin = c->transferReadyPin;
    cfg.dataAvailablePin = c->dataAvailablePin;
    cfg.sbcDataAvailablePin = c->sbcDataAvailablePin;
    cfg.isolateInterfaceThread = c->isolateInterfaceThread != 0;
    cfg.isolatedCoreId = c->isolatedCoreId;
    cfg.useRealtimeScheduling = c->useRealtimeScheduling != 0;
    cfg.interfaceRtPriority = c->interfaceRtPriority;
    cfg.sbcConnectTimeout = c->sbcConnectTimeout;
    cfg.sbcTransferTimeout = c->sbcTransferTimeout;
    cfg.sbcConnectionTimeout = c->sbcConnectionTimeout;
    cfg.sbcConnectionKeepAliveInterval = c->sbcConnectionKeepAliveInterval;
    cfg.maxSbcRetries = c->maxSbcRetries;
    return cfg;
}

} // namespace

extern "C" {

void DuetSbc_DefaultConfig(DuetSbcConfig *config) {
    if (config == nullptr) {
        return;
    }
    Config def;
    std::memset(config, 0, sizeof(*config));
    // String fields left null -> Create uses defaults. Numeric fields set from defaults.
    config->spiFrequency = def.spiFrequency;
    config->spiTransferMode = def.spiTransferMode;
    config->bufferSize = static_cast<int32_t>(def.bufferSize);
    config->transferReadyPin = def.transferReadyPin;
    config->dataAvailablePin = def.dataAvailablePin;
    config->sbcDataAvailablePin = def.sbcDataAvailablePin;
    config->isolateInterfaceThread = def.isolateInterfaceThread ? 1 : 0;
    config->isolatedCoreId = def.isolatedCoreId;
    config->useRealtimeScheduling = def.useRealtimeScheduling ? 1 : 0;
    config->interfaceRtPriority = def.interfaceRtPriority;
    config->sbcConnectTimeout = def.sbcConnectTimeout;
    config->sbcTransferTimeout = def.sbcTransferTimeout;
    config->sbcConnectionTimeout = def.sbcConnectionTimeout;
    config->sbcConnectionKeepAliveInterval = def.sbcConnectionKeepAliveInterval;
    config->maxSbcRetries = def.maxSbcRetries;
}

DuetSbcHandle *DuetSbc_Create(const DuetSbcConfig *config, char *errorBuf, int32_t errorBufLen) {
    try {
        return new DuetSbcHandle(FromC(config));
    } catch (const std::exception &e) {
        CopyError(errorBuf, errorBufLen, e.what());
        return nullptr;
    } catch (...) {
        CopyError(errorBuf, errorBufLen, "Unknown error creating SBC interface");
        return nullptr;
    }
}

void DuetSbc_SetRequestServedCallback(DuetSbcHandle *h, DuetSbcRequestServedCb cb, void *ctx) {
    if (h == nullptr) return;
    if (cb == nullptr) {
        h->interface.SetRequestServedCallback(nullptr);
        return;
    }
    h->interface.SetRequestServedCallback([cb, ctx](int64_t latencyNs) { cb(latencyNs, ctx); });
}

void DuetSbc_SetMessageCallback(DuetSbcHandle *h, DuetSbcMessageCb cb, void *ctx) {
    if (h == nullptr) return;
    if (cb == nullptr) {
        h->interface.SetMessageCallback(nullptr);
        return;
    }
    h->interface.SetMessageCallback([cb, ctx](uint32_t flags, const std::string &msg) {
        cb(flags, msg.data(), static_cast<int32_t>(msg.size()), ctx);
    });
}

void DuetSbc_SetCanResponseCallback(DuetSbcHandle *h, DuetSbcCanResponseCb cb, void *ctx) {
    if (h == nullptr) return;
    if (cb == nullptr) {
        h->interface.SetCanResponseCallback(nullptr);
        return;
    }
    h->interface.SetCanResponseCallback(
        [cb, ctx](const duet::sbc::protocol::CanResponseHeader &header, const uint8_t *payload) {
            cb(header.txToken, header.msgType, header.dataLength, header.srcAddress, header.flags,
               header.status, payload, ctx);
        });
}

int32_t DuetSbc_Connect(DuetSbcHandle *h, char *errorBuf, int32_t errorBufLen) {
    if (h == nullptr) return -1;
    try {
        h->interface.Connect();
        return 0;
    } catch (const std::exception &e) {
        CopyError(errorBuf, errorBufLen, e.what());
        return -1;
    } catch (...) {
        CopyError(errorBuf, errorBufLen, "Unknown error connecting");
        return -1;
    }
}

void DuetSbc_Start(DuetSbcHandle *h) {
    if (h != nullptr) h->interface.Start();
}

void DuetSbc_Stop(DuetSbcHandle *h) {
    if (h != nullptr) h->interface.Stop();
}

void DuetSbc_QueueMessage(DuetSbcHandle *h, uint32_t flags, const char *message, int32_t length) {
    if (h == nullptr) return;
    h->interface.QueueMessage(flags, std::string(message ? message : "", message ? length : 0));
}

void DuetSbc_QueueCanMessage(DuetSbcHandle *h, uint16_t txToken, uint16_t msgType, uint16_t replyType,
                             uint8_t dstAddress, int32_t isResponse, const uint8_t *payload, int32_t length) {
    if (h == nullptr) return;
    std::vector<uint8_t> data;
    if (payload != nullptr && length > 0) {
        data.assign(payload, payload + length);
    }
    h->interface.QueueCanMessage(txToken, msgType, replyType, dstAddress, isResponse != 0, std::move(data));
}

void DuetSbc_QueueEnableCan(DuetSbcHandle *h, int32_t enable) {
    if (h != nullptr) h->interface.QueueEnableCan(enable != 0);
}

void DuetSbc_RequestTransfer(DuetSbcHandle *h) {
    if (h != nullptr) h->interface.RequestTransfer();
}

double DuetSbc_GetMaxPinWaitMs(DuetSbcHandle *h) {
    return h != nullptr ? h->interface.Transfer().MaxPinWaitDurationMs() : 0.0;
}

double DuetSbc_GetMaxFullTransferDelayMs(DuetSbcHandle *h) {
    return h != nullptr ? h->interface.Transfer().MaxFullTransferDelayMs() : 0.0;
}

int32_t DuetSbc_GetTfrPinGlitches(DuetSbcHandle *h) {
    return h != nullptr ? h->interface.Transfer().TfrPinGlitches() : 0;
}

int32_t DuetSbc_GetMissedEdges(DuetSbcHandle *h) {
    return h != nullptr ? h->interface.Transfer().MissedEdges() : 0;
}

void DuetSbc_Destroy(DuetSbcHandle *h) {
    delete h;
}

} // extern "C"
