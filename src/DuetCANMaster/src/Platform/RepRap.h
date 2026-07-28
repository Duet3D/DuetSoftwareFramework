/****************************************************************************************************

RepRapFirmware - Reprap

RepRap is a simple class that acts as a container for an instance of all the others.

-----------------------------------------------------------------------------------------------------

Version 0.1

21 May 2013

Adrian Bowyer
RepRap Professional Ltd
http://reprappro.com

Licence: GPL

****************************************************************************************************/

#ifndef REPRAP_H
#define REPRAP_H

#include <RepRapFirmware.h>

#include <RTOSIface/RTOSIface.h>

#include <General/function_ref.h>

using DebugFlags = Bitmap<uint16_t>;

class RepRap final
{
  public:
	RepRap() noexcept;
	RepRap(const RepRap&) = delete;
	RepRap& operator=(const RepRap&) = delete;
	~RepRap() = default;

	void EmergencyStop() noexcept;
	void Init() noexcept;
	void Spin() noexcept;
	void Exit() noexcept;

	void Diagnostics(MessageType mtype, const StringRef& reply) noexcept;
	void Timing(const StringRef& reply) noexcept;

	[[nodiscard]] bool Debug(const Module& module) const noexcept
	{
		return m_debugMaps[module.ToBaseType()].IsNonEmpty();
	}
	[[nodiscard]] DebugFlags GetDebugFlags(const Module& m) const noexcept { return m_debugMaps[m.ToBaseType()]; }

	[[nodiscard]] Module GetSpinningModule() const noexcept;

	[[nodiscard]] Platform& GetPlatform() const noexcept { return *m_platform; }

	void LogDebugMessage(c_string msg, uint32_t data0, uint32_t data1, uint32_t data2, uint32_t data3) noexcept;

#if HAS_SBC_INTERFACE
	[[nodiscard]] SbcInterface& GetSbcInterface() const noexcept { return *m_sbcInterface; }
#endif
#if SUPPORT_CAN_EXPANSION
	[[nodiscard]] ExpansionManager& GetExpansion() const noexcept { return *m_expansion; }
#endif

	void Tick() noexcept;
	[[nodiscard]] bool SpinTimeoutImminent() const noexcept;
	[[nodiscard]] bool IsStopped() const noexcept;

#if 0 // removed because we ran out of flash memory on Duet 2
	OutputBuffer *_ecv_null GetStatusResponse(uint8_t type, ResponseSource source) const noexcept;
	OutputBuffer *_ecv_null GetConfigResponse() noexcept;
#endif

	void Beep(unsigned int freq, unsigned int ms) noexcept;

	[[nodiscard]] bool IsProcessingConfig() const noexcept { return m_processingConfig; }

	// Firmware update operations
	bool CheckFirmwareUpdatePrerequisites(const StringRef& reply, const StringRef& filenameRef) noexcept;
#if HAS_MASS_STORAGE
	void UpdateFirmware(c_string iapFilename, c_string iapParam) noexcept;
#endif
	void PrepareToLoadIap() noexcept;
	[[noreturn]] void StartIap(c_string _ecv_null filename) noexcept;

	void ReportInternalError(c_string file, c_string func, int line) const noexcept; // report an internal error

	static uint32_t DoDivide(uint32_t a, uint32_t b) noexcept; // helper function for diagnostic tests
	static void DoMemoryLeak() noexcept;					   // helper function for diagnostic tests
	static void GenerateBusFault() noexcept;				   // helper function for diagnostic tests
	static float SinfCosf(float angle) noexcept;			   // helper function for diagnostic tests

	void KickHeatTaskWatchdog() noexcept { m_heatTaskIdleTicks = 0; }

  private:
	__attribute__((noinline)) void GenerateDeferredDiagnostics(MessageType destination) noexcept;

#ifndef DUET_NG // Duet 2 doesn't currently need this feature, so omit it to save memory
	struct DebugLogRecord
	{
		c_string _ecv_null msg;
		uint32_t data[4]{};

		DebugLogRecord() noexcept
			: msg(nullptr)
		{
		}
	};
#endif

	static constexpr size_t NumDebugRecords = 4;

	static void EncodeString(StringRef& response,
							 c_string src,
							 size_t spaceToLeave,
							 bool allowControlChars = false,
							 char prefix = 0) noexcept;
	static void AppendFloatArray(OutputBuffer* buf,
								 c_string _ecv_null name,
								 size_t numValues,
								 function_ref_noexcept<float(size_t) noexcept> func,
								 unsigned int numDecimalDigits) noexcept;
	static void AppendIntArray(OutputBuffer* buf,
							   c_string _ecv_null name,
							   size_t numValues,
							   function_ref_noexcept<int(size_t) noexcept> func) noexcept;
	static void AppendStringArray(OutputBuffer* buf,
								  c_string _ecv_null name,
								  size_t numValues,
								  function_ref_noexcept<const char*(size_t) noexcept> func) noexcept;

	void ClearDebug() noexcept;

	static constexpr uint32_t MaxHeatTaskTicksInSpinState =
		4000; // timeout before we reset the processor if the heat task doesn't run
	static constexpr uint32_t MaxMainTaskTicksInSpinState =
		20000; // timeout before we reset the processor if the main task doesn't run
	static constexpr uint32_t HighMainTaskTicksInSpinState =
		16000; // how long before we warn that timeout is approaching

	Platform* m_platform{};

#if SUPPORT_IOBITS
	PortControl* m_portControl{};
#endif

#if HAS_SBC_INTERFACE
	SbcInterface* m_sbcInterface{};
#endif

#if SUPPORT_CAN_EXPANSION
	ExpansionManager* m_expansion{};
#endif

	uint16_t m_boardsSeq, m_directoriesSeq, m_fansSeq, m_heatSeq, m_inputsSeq, m_jobSeq, m_ledStripsSeq, m_moveSeq,
		m_globalSeq;
	uint16_t m_networkSeq, m_sensorsSeq, m_spindlesSeq, m_stateSeq, m_toolsSeq, m_volumesSeq;

	uint32_t m_lastWarningMillis; // when we last sent a warning message for things that can happen very often

	uint16_t m_ticksInSpinState;
	uint16_t m_heatTaskIdleTicks;
	uint32_t m_fastLoop{}, m_slowLoop{};

	DebugFlags m_debugMaps[Module::NumModules];

	unsigned int m_beepFrequency, m_beepDuration;
	uint32_t m_beepTimer;

	// State flags
	Module m_spinningModule;
	bool m_stopped;
	bool m_active;
	bool m_processingConfig;
};

// A single instance of the RepRap class contains all the others
extern RepRap reprap;

inline Module RepRap::GetSpinningModule() const noexcept
{
	return m_spinningModule;
}
inline bool RepRap::IsStopped() const noexcept
{
	return m_stopped;
}

#endif
