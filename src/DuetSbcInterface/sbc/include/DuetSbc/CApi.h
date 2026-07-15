// C ABI wrapping the SBC interface so it can be consumed from C# via P/Invoke (or any other
// language). Kept intentionally small and free of C++ types on the boundary. Built into
// libduet_sbc_shared.so. All functions are thread-safe with respect to distinct handles.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct DuetSbcHandle DuetSbcHandle;

// Configuration passed across the ABI. Mirrors duet::sbc::Config. Any string may be null to use the
// built-in default.
typedef struct {
    const char *spiDevice;
    uint32_t spiFrequency;
    int32_t spiTransferMode;
    int32_t bufferSize;

    const char *gpioChipDevice;
    int32_t transferReadyPin;
    int32_t dataAvailablePin;
    int32_t sbcDataAvailablePin; // < 0 disables the scope-trigger output line

    int32_t isolateInterfaceThread; // bool
    int32_t isolatedCoreId;
    int32_t useRealtimeScheduling; // bool
    int32_t interfaceRtPriority;

    int32_t sbcConnectTimeout;
    int32_t sbcTransferTimeout;
    int32_t sbcConnectionTimeout;
    int32_t sbcConnectionKeepAliveInterval;
    int32_t maxSbcRetries;
} DuetSbcConfig;

// Callback types (all receive the user context registered alongside them).
typedef void (*DuetSbcRequestServedCb)(int64_t latencyNs, void *ctx);
typedef void (*DuetSbcMessageCb)(uint32_t flags, const char *message, int32_t length, void *ctx);
typedef void (*DuetSbcCanResponseCb)(uint16_t txToken, uint16_t msgType, uint16_t dataLength,
                                     uint8_t srcAddress, uint8_t flags, uint8_t status,
                                     const uint8_t *payload, void *ctx);

// Fill `config` with the default values.
void DuetSbc_DefaultConfig(DuetSbcConfig *config);

// Create an interface instance. Returns null on failure and writes an error message into
// errorBuf (if non-null). The instance must be freed with DuetSbc_Destroy.
DuetSbcHandle *DuetSbc_Create(const DuetSbcConfig *config, char *errorBuf, int32_t errorBufLen);

// Register callbacks (may be null to clear). Must be called before DuetSbc_Start.
void DuetSbc_SetRequestServedCallback(DuetSbcHandle *h, DuetSbcRequestServedCb cb, void *ctx);
void DuetSbc_SetMessageCallback(DuetSbcHandle *h, DuetSbcMessageCb cb, void *ctx);
void DuetSbc_SetCanResponseCallback(DuetSbcHandle *h, DuetSbcCanResponseCb cb, void *ctx);

// Connect to the firmware (blocking). Returns 0 on success, non-zero on failure (message in errorBuf).
int32_t DuetSbc_Connect(DuetSbcHandle *h, char *errorBuf, int32_t errorBufLen);

// Start / stop the transfer loop.
void DuetSbc_Start(DuetSbcHandle *h);
void DuetSbc_Stop(DuetSbcHandle *h);

// Queue outgoing data (returns immediately; the transfer loop sends it).
void DuetSbc_QueueMessage(DuetSbcHandle *h, uint32_t flags, const char *message, int32_t length);
void DuetSbc_QueueCanMessage(DuetSbcHandle *h, uint16_t txToken, uint16_t msgType, uint16_t replyType,
                             uint8_t dstAddress, int32_t isResponse, const uint8_t *payload, int32_t length);
void DuetSbc_QueueEnableCan(DuetSbcHandle *h, int32_t enable);
void DuetSbc_RequestTransfer(DuetSbcHandle *h);

// Diagnostics.
double DuetSbc_GetMaxPinWaitMs(DuetSbcHandle *h);
double DuetSbc_GetMaxFullTransferDelayMs(DuetSbcHandle *h);
int32_t DuetSbc_GetTfrPinGlitches(DuetSbcHandle *h);
int32_t DuetSbc_GetMissedEdges(DuetSbcHandle *h);

// Destroy the instance (stops the loop first).
void DuetSbc_Destroy(DuetSbcHandle *h);

#ifdef __cplusplus
}
#endif
