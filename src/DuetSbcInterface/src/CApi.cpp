#include "CApi.h"

#include <Config/Configuration.h>
#include <Motion/StepTimer.h>
#include <Motion/MotionService.h>
#include <Interface/LinkService.h>
#include <Interface/SPI/SpiTransfer.h>
#include <Interface/TransportFactory.h>

#include <algorithm>
#include <cstring>
#include <exception>
#include <string>

using Duet::Sbc::Config;
using Duet::Sbc::LinkService;

struct DuetSbcHandle
{
	Config config;
	LinkService interface;
	Duet::Sbc::MotionService motion;
	explicit DuetSbcHandle(const Config& cfg)
		: config(cfg)
		, interface(cfg, Duet::Sbc::CreateTransport(cfg))
		, motion(interface)
	{
	}
};

namespace
{

	void CopyError(char* buf, int32_t len, const std::string& msg)
	{
		if (buf != nullptr && len > 0)
		{
			const size_t n = std::min<size_t>(msg.size(), static_cast<size_t>(len - 1));
			std::memcpy(buf, msg.data(), n);
			buf[n] = '\0';
		}
	}

	Config FromC(const DuetSbcConfig* c)
	{
		Config cfg;
		if (c == nullptr)
		{
			return cfg;
		}
		if (c->spiDevice)
			cfg.spiDevice = c->spiDevice;
		if (c->spiFrequency)
			cfg.spiFrequency = c->spiFrequency;
		cfg.spiTransferMode = c->spiTransferMode;
		if (c->bufferSize > 0)
			cfg.bufferSize = static_cast<size_t>(c->bufferSize);
		if (c->gpioChipDevice)
			cfg.gpioChipDevice = c->gpioChipDevice;
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
		cfg.updateOnly = c->updateOnly != 0;
		return cfg;
	}

} // namespace

extern "C"
{

	void DuetSbc_DefaultConfig(DuetSbcConfig* config)
	{
		if (config == nullptr)
		{
			return;
		}
		const Config def;
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
		config->updateOnly = def.updateOnly ? 1 : 0;
	}

	DuetSbcHandle* DuetSbc_Create(const DuetSbcConfig* config, char* errorBuf, int32_t errorBufLen)
	{
		try
		{
			return new DuetSbcHandle(FromC(config));
		}
		catch (const std::exception& e)
		{
			CopyError(errorBuf, errorBufLen, e.what());
			return nullptr;
		}
		catch (...)
		{
			CopyError(errorBuf, errorBufLen, "Unknown error creating SBC interface");
			return nullptr;
		}
	}

	int32_t DuetSbc_Connect(DuetSbcHandle* h, char* errorBuf, int32_t errorBufLen)
	{
		if (h == nullptr)
			return -1;
		try
		{
			h->interface.Connect();
			return 0;
		}
		catch (const std::exception& e)
		{
			CopyError(errorBuf, errorBufLen, e.what());
			return -1;
		}
		catch (...)
		{
			CopyError(errorBuf, errorBufLen, "Unknown error connecting");
			return -1;
		}
	}

	void DuetSbc_Start(DuetSbcHandle* h)
	{
		if (h != nullptr)
			h->interface.Start();
	}

	void DuetSbc_Stop(DuetSbcHandle* h)
	{
		if (h != nullptr)
			h->interface.Stop();
	}

	// --- Outbound ---

	int64_t DuetSbc_QueueMessage(DuetSbcHandle* h, uint32_t flags, const char* message, int32_t length)
	{
		if (h == nullptr)
			return -1;
		const uint32_t seq = h->interface.QueueMessage(
			flags, message, (message != nullptr && length > 0) ? static_cast<size_t>(length) : 0);
		return (seq != 0) ? static_cast<int64_t>(seq) : -1;
	}

	int64_t DuetSbc_QueueCanMessage(DuetSbcHandle* h,
									uint16_t txToken,
									uint16_t msgType,
									uint16_t replyType,
									uint8_t dstAddress,
									int32_t isResponse,
									const uint8_t* payload,
									int32_t length)
	{
		if (h == nullptr)
			return -1;
		const uint32_t seq =
			h->interface.QueueCanMessage(txToken,
										 msgType,
										 replyType,
										 dstAddress,
										 isResponse != 0,
										 payload,
										 (payload != nullptr && length > 0) ? static_cast<size_t>(length) : 0);
		return (seq != 0) ? static_cast<int64_t>(seq) : -1;
	}

	int64_t DuetSbc_QueueEnableCan(DuetSbcHandle* h, int32_t enable, uint32_t requestId)
	{
		if (h == nullptr)
			return -1;
		const uint32_t seq = h->interface.QueueEnableCan(enable != 0, requestId);
		return (seq != 0) ? static_cast<int64_t>(seq) : -1;
	}

	void DuetSbc_RequestEmergencyStop(DuetSbcHandle* h, uint32_t requestId)
	{
		if (h != nullptr)
			h->interface.RequestEmergencyStop(requestId);
	}

	void DuetSbc_RequestReset(DuetSbcHandle* h, uint32_t requestId)
	{
		if (h != nullptr)
			h->interface.RequestReset(requestId);
	}

	int32_t DuetSbc_RequestFirmwareUpdate(DuetSbcHandle* h,
										  const uint8_t* iap,
										  int32_t iapLength,
										  const uint8_t* firmware,
										  int32_t firmwareLength,
										  uint16_t firmwareCrc16,
										  uint32_t requestId)
	{
		if (h == nullptr || iapLength <= 0 || firmwareLength <= 0)
			return -1;
		return h->interface.RequestFirmwareUpdate(iap,
												  static_cast<size_t>(iapLength),
												  firmware,
												  static_cast<size_t>(firmwareLength),
												  firmwareCrc16,
												  requestId)
				   ? 0
				   : -1;
	}

	void DuetSbc_RequestTransfer(DuetSbcHandle* h)
	{
		if (h != nullptr)
			h->interface.RequestTransfer();
	}

	// --- Inbound ---

	int32_t DuetSbc_PeekEvent(DuetSbcHandle* h, const uint8_t** data, int32_t* length)
	{
		if (h == nullptr || data == nullptr || length == nullptr)
			return 0;
		const std::optional<Duet::Sbc::ByteSpan> record = h->interface.Inbound().Peek();
		if (!record.has_value())
		{
			return 0;
		}
		*data = record->data();
		*length = static_cast<int32_t>(record->size());
		return 1;
	}

	void DuetSbc_ConsumeEvent(DuetSbcHandle* h)
	{
		if (h != nullptr)
			h->interface.Inbound().Consume();
	}

	int32_t DuetSbc_WaitForEvent(DuetSbcHandle* h, int32_t timeoutMs)
	{
		if (h == nullptr)
			return 0;
		return h->interface.WaitForInbound(timeoutMs) ? 1 : 0;
	}

	// --- Diagnostics ---

	int32_t DuetSbc_GetProtocolVersion(DuetSbcHandle* h)
	{
		return h != nullptr ? h->interface.Transfer().ProtocolVersion() : 0;
	}

	double DuetSbc_GetMaxPinWaitMs(DuetSbcHandle* h)
	{
		return h != nullptr ? h->interface.Transfer().MaxPinWaitDurationMs() : 0.0;
	}

	double DuetSbc_GetMaxFullTransferDelayMs(DuetSbcHandle* h)
	{
		return h != nullptr ? h->interface.Transfer().MaxFullTransferDelayMs() : 0.0;
	}

	int32_t DuetSbc_GetTfrPinGlitches(DuetSbcHandle* h)
	{
		const auto* spi = (h != nullptr) ? dynamic_cast<const Duet::Sbc::SpiTransfer*>(&h->interface.Transfer()) : nullptr;
		return (spi != nullptr) ? spi->TfrPinGlitches() : 0;
	}

	int32_t DuetSbc_GetMissedEdges(DuetSbcHandle* h)
	{
		const auto* spi = (h != nullptr) ? dynamic_cast<const Duet::Sbc::SpiTransfer*>(&h->interface.Transfer()) : nullptr;
		return (spi != nullptr) ? spi->MissedEdges() : 0;
	}

	int32_t DuetSbc_GetResyncCount(DuetSbcHandle* h)
	{
		return h != nullptr ? h->interface.Transfer().ResyncCount() : 0;
	}

	uint64_t DuetSbc_GetDroppedEvents(DuetSbcHandle* h)
	{
		return h != nullptr ? h->interface.Inbound().DroppedRecords() : 0;
	}

	int32_t DuetSbc_MotionConfigure(DuetSbcHandle* h, const void* config, int32_t length)
	{
		if (h == nullptr || config == nullptr || length != (int32_t)sizeof(Duet::Sbc::Motion::MotionConfig))
		{
			return 0;
		}
		Duet::Sbc::Motion::MotionConfig copy{};
		std::memcpy(&copy, config, sizeof(copy));
		h->motion.Configure(copy);
		return 1;
	}

	int32_t DuetSbc_MotionStart(DuetSbcHandle* h, int32_t rtPriority)
	{
		if (h == nullptr || !h->motion.Init())
		{
			return 0;
		}
		h->motion.Start(rtPriority);
		return 1;
	}

	void DuetSbc_MotionStop(DuetSbcHandle* h)
	{
		if (h != nullptr)
		{
			h->motion.Stop();
		}
	}

	int32_t DuetSbc_MotionCanAddMove(DuetSbcHandle* h, int32_t ring)
	{
		return (h != nullptr && ring >= 0 && h->motion.CanAddMove((unsigned int)ring)) ? 1 : 0;
	}

	// This is a C ABI, so the pointer/length pairs stay as they are. It is also the only place they
	// exist: each one is turned into a span here, once, and everything inside the library carries
	// the bound with the pointer from then on.

	int32_t DuetSbc_MotionSubmitMove(DuetSbcHandle* h, const void* moveParams, int32_t length)
	{
		if (h == nullptr || moveParams == nullptr || length <= 0)
		{
			return 0;
		}
		return h->motion.SubmitMove({static_cast<const uint8_t*>(moveParams), (size_t)length}) ? 1 : 0;
	}

	int32_t DuetSbc_MotionGetMotorPositions(DuetSbcHandle* h, int32_t* stepsOut, int32_t count, uint32_t* whenTicks)
	{
		if (h == nullptr || stepsOut == nullptr || count <= 0)
		{
			return 0;
		}
		return (int32_t)h->motion.GetMotorPositions({stepsOut, (size_t)count}, whenTicks);
	}

	int32_t DuetSbc_MotionGetLivePositions(DuetSbcHandle* h, int32_t* stepsOut, int32_t count, uint32_t* whenTicks)
	{
		if (h == nullptr || stepsOut == nullptr || count <= 0)
		{
			return 0;
		}
		return (int32_t)h->motion.GetLivePositions({stepsOut, (size_t)count}, whenTicks);
	}

	int32_t DuetSbc_MotionGetPositionAt(DuetSbcHandle* h, int32_t drive, uint32_t whenTicks,
										int32_t* positionOut, int32_t* positionAtMoveStartOut,
										int32_t* usedTimestampOut)
	{
		if (h == nullptr || drive < 0 || positionOut == nullptr || positionAtMoveStartOut == nullptr
			|| usedTimestampOut == nullptr)
		{
			return 0;
		}

		bool usedTimestamp = false;
		if (!h->motion.GetPositionAt((size_t)drive, whenTicks, *positionOut, *positionAtMoveStartOut, usedTimestamp))
		{
			return 0;
		}
		*usedTimestampOut = usedTimestamp ? 1 : 0;
		return 1;
	}

	int32_t DuetSbc_MotionSetMotorPositions(DuetSbcHandle* h, uint32_t driveMask, const int32_t* positions, int32_t count)
	{
		if (h == nullptr || positions == nullptr || count <= 0)
		{
			return 0;
		}
		return h->motion.SetMotorPositions(driveMask, {positions, (size_t)count}) ? 1 : 0;
	}

	void DuetSbc_MotionSetRingState(DuetSbcHandle* h, int32_t ring, int32_t shouldStartMove, int32_t waitingForEmpty)
	{
		if (h != nullptr && ring >= 0)
		{
			h->motion.SetRingState((unsigned int)ring, shouldStartMove != 0, waitingForEmpty != 0);
		}
	}

	uint32_t DuetSbc_MotionGetScheduledMoves(DuetSbcHandle* h, int32_t ring)
	{
		return (h != nullptr && ring >= 0) ? h->motion.GetScheduledMoves((unsigned int)ring) : 0;
	}

	uint32_t DuetSbc_MotionGetCompletedMoves(DuetSbcHandle* h, int32_t ring)
	{
		return (h != nullptr && ring >= 0) ? h->motion.GetCompletedMoves((unsigned int)ring) : 0;
	}

	uint32_t DuetSbc_MotionGetSubmissionsDropped(DuetSbcHandle* h)
	{
		return (h != nullptr) ? h->motion.GetSubmissionsDropped() : 0;
	}

	uint32_t DuetSbc_MotionGetForcedPositionsApplied(DuetSbcHandle* h)
	{
		return (h != nullptr) ? h->motion.GetForcedPositionsApplied() : 0;
	}

	int32_t DuetSbc_MotionHasPendingSubmissions(DuetSbcHandle* h)
	{
		return (h != nullptr && h->motion.HasPendingSubmissions()) ? 1 : 0;
	}

	uint32_t DuetSbc_GetStepClockTicks(DuetSbcHandle* h)
	{
		(void)h;					// the model is process-wide, like the clock it tracks
		return StepTimer::GetTimerTicks();
	}

	uint32_t DuetSbc_GetMovementDelay(DuetSbcHandle* h)
	{
		(void)h;					// as above
		return StepTimer::GetMovementDelay();
	}

	void DuetSbc_GetClockStats(DuetSbcHandle* h, DuetSbcClockStats* stats)
	{
		(void)h;
		if (stats == nullptr)
		{
			return;
		}
		const StepTimer::ClockStats source = StepTimer::GetClockStats();
		stats->driftPpm = source.driftPpm;
		stats->numSamples = source.numSamples;
		stats->peakResidualNs = source.peakResidualNs;
		stats->numBackwardClamps = source.numBackwardClamps;
		stats->numRejectedSamples = source.numRejectedSamples;
		stats->synced = source.synced ? 1 : 0;
	}

	void DuetSbc_Destroy(DuetSbcHandle* h)
	{
		delete h;
	}

} // extern "C"
