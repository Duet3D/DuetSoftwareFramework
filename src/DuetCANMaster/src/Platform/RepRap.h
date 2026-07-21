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

enum class ResponseSource
{
	HTTP,
	AUX,
	Generic
};

typedef Bitmap<uint16_t> DebugFlags;

class RepRap final
{
  public:
	RepRap() noexcept;
	RepRap(const RepRap&) = delete;

	void EmergencyStop() noexcept;
	void Init() noexcept;
	void Spin() noexcept;
	void Exit() noexcept;

	void Diagnostics(MessageType mtype, const StringRef& reply) noexcept;
	void Timing(const StringRef& reply) noexcept;

	bool Debug(Module module) const noexcept { return debugMaps[module.ToBaseType()].IsNonEmpty(); }
	DebugFlags GetDebugFlags(Module m) const noexcept { return debugMaps[m.ToBaseType()]; }

	Module GetSpinningModule() const noexcept;

	Platform& GetPlatform() const noexcept { return *platform; }

	void LogDebugMessage(c_string msg, uint32_t data0, uint32_t data1, uint32_t data2, uint32_t data3) noexcept;

#if HAS_SBC_INTERFACE
	SbcInterface& GetSbcInterface() const noexcept { return *sbcInterface; }
#endif
#if SUPPORT_CAN_EXPANSION
	ExpansionManager& GetExpansion() const noexcept { return *expansion; }
#endif

	void Tick() noexcept;
	bool SpinTimeoutImminent() const noexcept;
	bool IsStopped() const noexcept;

#if 0 // removed because we ran out of flash memory on Duet 2
	OutputBuffer *_ecv_null GetStatusResponse(uint8_t type, ResponseSource source) const noexcept;
	OutputBuffer *_ecv_null GetConfigResponse() noexcept;
#endif

	void Beep(unsigned int freq, unsigned int ms) noexcept;

	bool IsProcessingConfig() const noexcept { return processingConfig; }

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

	void KickHeatTaskWatchdog() noexcept { heatTaskIdleTicks = 0; }

  private:
	__attribute__((noinline)) void GenerateDeferredDiagnostics(MessageType destination) noexcept;

#ifndef DUET_NG // Duet 2 doesn't currently need this feature, so omit it to save memory
	struct DebugLogRecord
	{
		c_string _ecv_null msg;
		uint32_t data[4];

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

	Platform* platform{};

#if SUPPORT_IOBITS
	PortControl* portControl{};
#endif

#if HAS_SBC_INTERFACE
	SbcInterface* sbcInterface{};
#endif

#if SUPPORT_CAN_EXPANSION
	ExpansionManager* expansion{};
#endif

	uint16_t boardsSeq, directoriesSeq, fansSeq, heatSeq, inputsSeq, jobSeq, ledStripsSeq, moveSeq, globalSeq;
	uint16_t networkSeq, sensorsSeq, spindlesSeq, stateSeq, toolsSeq, volumesSeq;

	uint32_t lastWarningMillis; // when we last sent a warning message for things that can happen very often

	uint16_t ticksInSpinState;
	uint16_t heatTaskIdleTicks;
	uint32_t fastLoop{}, slowLoop{};

	DebugFlags debugMaps[Module::numModules];

	unsigned int beepFrequency, beepDuration;
	uint32_t beepTimer;

	// State flags
	Module spinningModule;
	bool stopped;
	bool active;
	bool processingConfig;
};

// A single instance of the RepRap class contains all the others
extern RepRap reprap;

inline Module RepRap::GetSpinningModule() const noexcept
{
	return spinningModule;
}
inline bool RepRap::IsStopped() const noexcept
{
	return stopped;
}

#ifndef DUET_NG // Duet 2 doesn't currently need this feature, so omit it to save memory

// Class to watch an area of memory to detect corruption and (if possible) correct it
// Used in class WiFiInterface on the SAME5x
template <size_t NumWords>
class MemoryWatcher
{
  public:
	__attribute__((noinline)) explicit MemoryWatcher(uint32_t* _ecv_array p_address) noexcept;
	__attribute__((noinline)) MemoryWatcher() noexcept;
	~MemoryWatcher() noexcept;
	__attribute__((noinline)) bool Check(unsigned int tag) noexcept;

  private:
	void Init() noexcept;

	volatile uint32_t* _ecv_array checkedData;
	uint32_t checkSum;
	volatile uint32_t dataCopy[NumWords];
};

// Constructor to watch memory at a specified start address
template <size_t NumWords>
MemoryWatcher<NumWords>::MemoryWatcher(uint32_t* _ecv_array p_address) noexcept
	: checkedData(p_address)
{
	Init();
}

// Constructor to watch memory immediately after the memory occupied by this memory watcher object
template <size_t NumWords>
MemoryWatcher<NumWords>::MemoryWatcher() noexcept
{
	checkedData = reinterpret_cast<uint32_t * _ecv_array>(this) + (sizeof(*this) / sizeof(uint32_t));
	Init();
}

template <size_t NumWords>
void MemoryWatcher<NumWords>::Init() noexcept
{
	// Copy the checked data across to our own storage, also compute and store a check word
	uint32_t csum = 0;
	for (size_t i = 0; i < NumWords; ++i)
	{
		const uint32_t val = checkedData[i]; // read volatile data just once
		dataCopy[i] = val;
		csum ^= val;
	}
	checkSum = csum;
}

template <size_t NumWords>
MemoryWatcher<NumWords>::~MemoryWatcher() noexcept
{
	// Nothing to do here unless we set debug breakpoints on the checked memory in the constructor, or we want to check
	// automatically on exit
}

// Check whether the memory concerned still equals the reference copy, print a debug message and return true if it has
// changed, else return false
template <size_t NumWords>
bool MemoryWatcher<NumWords>::Check(unsigned int tag) noexcept
{
	uint32_t csumProtected = 0;
	uint32_t csumCopy = 0;
	int badOffset = -1;
	;
	for (size_t i = 0; i < NumWords; ++i)
	{
		const uint32_t valProtected = checkedData[i]; // read volatile data just once
		const uint32_t valCopy = dataCopy[i];		  // read volatile data just once
		csumProtected ^= valProtected;				  // update new checksum of checked memory
		csumCopy ^= valCopy;						  // update new checksum of the copy of the checked memory
		if (valProtected != valCopy)				  // if the protected word and its copy are no longer the same
		{
			badOffset = (int)i;
		}
	}

	// If we found a difference, test whether the protected memory or the copy got changed. If t was the protected
	// memory, restore it from the copy.
	if (badOffset >= 0 || csumProtected != checkSum || csumCopy != checkSum)
	{
		const bool fix = (csumProtected != checkSum && csumCopy == checkSum);
		constexpr c_string msg =
			"Mem diff: offset %u, original %08" PRIx32 ", copy %08" PRIx32 ", flags %08" PRIx32 "\n";
		const uint32_t flags = ((csumProtected == checkSum) ? 0u : 1u) | ((csumCopy == checkSum) ? 0u : 0x10u) |
							   ((fix) ? 0x0100u : 0u) | (tag << 16);
		reprap.LogDebugMessage(msg, (unsigned int)badOffset * 4, checkedData[badOffset], dataCopy[badOffset], flags);

		if (fix)
		{
			// Try to mend the memory corruption
			memcpyu32(const_cast<uint32_t * _ecv_array>(checkedData),
					  const_cast<const uint32_t * _ecv_array>(dataCopy),
					  NumWords);
		}
		return true;
	}
	return false;
}

#endif

#if SAME5x

// Class to CRC memory to detect memory corruption
// How to use it:
// 1. Declare an object of type MemoryChecker
// 2. Disable interrupts.
// 3. Call the Init function passing the start and end addresses. The checked addresses must not include MemoryChecker
// object, the bottom of the current stack, or any buffers that are the target of DMA.
// 4. Perform the operation that is suspected of corrupting memory.
// 5. Call the Check function.
// 6. Enable interrupts.
// 7. Call the Report function to report a change in the memory, if there was one.
class MemoryChecker
{
  public:
	MemoryChecker() noexcept {}
	void Init(const uint32_t* _ecv_array p_start, const uint32_t* _ecv_array p_end) noexcept;
	void Check() noexcept;
	void Report(uint32_t tag) noexcept;

	uint32_t GetStartAddress() const noexcept { return reinterpret_cast<uint32_t>(start); }
	uint32_t GetEndAddress() const noexcept { return reinterpret_cast<uint32_t>(end); }
	bool HasFault() const noexcept { return fault; }

  private:
	const uint32_t* _ecv_array start;
	const uint32_t* _ecv_array end;
	uint32_t crc;
	bool fault;
};

#endif

#endif
