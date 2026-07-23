/****************************************************************************************************

RepRapFirmware - Platform

Platform contains all the code and definitions to deal with machine-dependent things such as control
pins, bed area, number of extruders, tolerable accelerations and speeds and so on.

No definitions that are system-independent should go in here.  Put them in Configuration.h.

-----------------------------------------------------------------------------------------------------

Version 0.3

28 August 2013

Adrian Bowyer
RepRap Professional Ltd
http://reprappro.com

Licence: GPL

****************************************************************************************************/

#ifndef PLATFORM_H
#define PLATFORM_H

#include <RepRapFirmware.h>

#include <Hardware/IoPorts.h>
#include <SPI/SharedSpiDevice.h>

#include <TemperatureError.h>

#include "OutputMemory.h"
#include "UniqueId.h"

#include "AveragingFilter.h"
#include <General/IPAddress.h>
#include <General/function_ref.h>

#if SUPPORT_CAN_EXPANSION
#  include <CanMessageFormats.h>
#  include <RemoteInputHandle.h>
#endif

// Define the number of ADC filters and the indices of the extra ones
// Note, the thermistor code assumes that the first N filters are used by the TEMP0 to TEMP(N-1) thermistor inputs,
// where N = NumThermistorInputs
#if HAS_VREF_MONITOR
constexpr size_t VrefFilterIndex = NumThermistorInputs;
constexpr size_t VssaFilterIndex = NumThermistorInputs + 1;
#  if HAS_CPU_TEMP_SENSOR && !SAME5x
constexpr size_t CpuTempFilterIndex = NumThermistorInputs + 2;
constexpr size_t NumAdcFilters = NumThermistorInputs + 3;
#  else
constexpr size_t NumAdcFilters = NumThermistorInputs + 2;
#  endif
#elif HAS_CPU_TEMP_SENSOR && !SAME5x
constexpr size_t CpuTempFilterIndex = NumThermistorInputs;
constexpr size_t NumAdcFilters = NumThermistorInputs + 1;
#else
constexpr size_t NumAdcFilters = NumThermistorInputs;
#endif

// Z PROBE
constexpr unsigned int ZProbeAverageReadings =
	8; // We average this number of readings with IR on, and the same number with IR off

// HEATERS - The bed is assumed to be the at index 0

// Define the number of temperature readings we average for each thermistor. This should be a power of 2 and at least 4
// ^ AD_OVERSAMPLE_BITS.
#if SAME70
// On the SAME70 we read a thermistor on every tick so that we can average a higher number of readings
// Keep THERMISTOR_AVERAGE_READINGS * NUM_HEATERS * 1ms no greater than HEAT_SAMPLE_TIME or the PIDs won't work well.
constexpr unsigned int ThermistorAverageReadings = 16;
#else
// We read a thermistor on alternate ticks
// Keep THERMISTOR_AVERAGE_READINGS * NUM_HEATERS * 2ms no greater than HEAT_SAMPLE_TIME or the PIDs won't work well.
constexpr unsigned int ThermistorAverageReadings = 16;
#endif

#if SAME5x
constexpr unsigned int TempSenseAverageReadings = 16;
#endif

constexpr uint32_t maxPidSpinDelay = 5000; // Maximum elapsed time in milliseconds between successive temp samples by
										   // Pid::Spin() permitted for a temp sensor

/****************************************************************************************************/

enum class BoardType : uint8_t
{
	Auto = 0,			   // this value is no longer used
#if defined(DUET3MINI_V04) // we use the same values for both v0.2 and v0.4
	Duet3Mini_Unknown,
	Duet3Mini_WiFi, // Duet Mini WiFi with ESP8266 module
	Duet3Mini_Ethernet,
	Duet3Mini_WiFi_ESP32, // Duet Mini WiFi with ESP32 module
#elif defined(DUET3_MB6HC)
	Duet36HcV06100 = 1,
	Duet36HcV101 = 2,
	Duet36HcV102 = 3,
	Duet36HcV102b = 4,
	Duet36HcV102c = 5,
#elif defined(DUET3_MB6XD)
	Duet36XdV01 = 1,
	Duet36XdV100 = 2,
	Duet36XdV101 = 3,
	Duet36XdV102 = 4,
#elif defined(FMDC_V03)
	FMDC,
#elif defined(DUET_NG)
	DuetWiFi_10 = 1,
	DuetWiFi_102 = 2,
	DuetEthernet_10 = 3,
	DuetEthernet_102 = 4,
	Duet2SBC_10 = 5,
	Duet2SBC_102 = 6,
#elif defined(DUET_M)
	DuetM_10 = 1,
#elif defined(PCCB_10)
	PCCB_v10 = 1
#elif defined(INDX)
	Indx,
#else
#  error Unknown board
#endif
};

/***************************************************************************************************/

// Enumeration to describe various tests we do in response to the M122 command
// NOLINTNEXTLINE(performance-enum-size)
enum class DiagnosticTestType : unsigned int
{
	PrintTestReport = 1, // run some tests and report the processor ID

	PrintMoves = 100, // print summary of recent moves (only if recording moves was enabled in firmware)
#ifdef DUET_NG
	PrintExpanderStatus = 101, // print DueXn expander status
#endif
	TimeCalculations = 102,		// do a timing test on the square root function and sine/cosine
	Unused1 = 103,				// was TimeSinCos
	TimeSDWrite = 104,			// do a write timing test on the SD card
	PrintObjectSizes = 105,		// print the sizes of various objects
	PrintObjectAddresses = 106, // print the addresses and sizes of various objects
	TimeCRC32 = 107,			// time how long it takes to calculate CRC32
	TimeGetTimerTicks = 108,	// time now long it takes to read the step clock
	UndervoltageEvent = 109,	// pretend an undervoltage condition has occurred
#if SUPPORT_S_CURVE
	TimeCubicSolver = 110,
	TimeQuarticSolver = 111,
#endif

	SetWriteBuffer = 500, // enable/disable the write buffer

	OutputBufferStarvation = 900, // Allocate almost all output buffers to emulate starvation

	TestWatchdog = 1001,		  // test that we get a watchdog reset if the tick interrupt stops
	TestSpinLockup = 1002,		  // test that we get a software reset if a Spin() function takes too long
	TestSerialBlock = 1003,		  // test what happens when we write a blocking message via debugPrintf()
	DivideByZero = 1004,		  // do an integer divide by zero to test exception handling
	UnalignedMemoryAccess = 1005, // do an unaligned memory access to test exception handling
	BusFault = 1006,			  // generate a bus fault
	AccessMemory = 1007,		  // read or write  memory
	MemoryLeak = 1008			  // cause an out of memory fault
};

/***************************************************************************************************************/

using ThermistorAveragingFilter = AveragingFilter<ThermistorAverageReadings>;

// Enumeration of error condition bits
// NOLINTNEXTLINE(performance-enum-size)
enum class ErrorCode : uint32_t
{
	BadTemp = 1u << 0,
	BadMove = 1u << 1,
	OutputStarvation = 1u << 2,
	OutputStackOverflow = 1u << 3,
	HsmciTimeout = 1u << 4
};

#if HAS_MASS_STORAGE || HAS_EMBEDDED_FILES || HAS_SBC_INTERFACE

// Class to manage a configurable folder, used for the sys and web folders
class ConfigurableFolder
{
  public:
	explicit ConfigurableFolder(const char* _ecv_array defValue) noexcept
		: m_userValue(nullptr)
		, m_defaultValue(defValue)
	{
	}
	ReadLockedPointer<const char> GetLockedPointer() const noexcept;
#  if HAS_MASS_STORAGE || HAS_EMBEDDED_FILES
	void AppendToString(const StringRef& path) const noexcept;
	GCodeResult Configure(const char* _ecv_array newPath, const StringRef& reply) noexcept;
#  endif
  private:
	mutable ReadWriteLock m_lock;
	const char* _ecv_array GetUnlockedPointer() const noexcept
	{
		return (m_userValue == nullptr) ? m_defaultValue : _ecv_not_null(m_userValue);
	}
	const char* _ecv_array _ecv_null m_userValue;
	const char* _ecv_array m_defaultValue;
};

#endif

// The main class that defines the RepRap machine for the benefit of the other classes
class Platform final
{
  public:
	Platform() noexcept;
	Platform(const Platform&) = delete;
	Platform& operator=(const Platform&) = delete;
	~Platform() = default;

	//-------------------------------------------------------------------------------------------------------------

	// These are the functions that form the interface between Platform and the rest of the firmware.

	void Init() noexcept; // Set the machine up after a restart.  If called subsequently this should set the machine up
						  // as if it has just been restarted; it can do this by executing an actual restart if you
						  // like, but beware the loop of death...
	void Spin() noexcept; // This gets called in the main loop and should do any housekeeping needed
	void Exit() noexcept; // Shut down tidily. Calling Init after calling this should reset to the beginning

	void Diagnostics(unsigned int part, const StringRef& reply) noexcept;
	static constexpr unsigned int NumPlatformDiagnosticParts = 7;

	static SharedSpiDevice& GetSharedSpiDevice() noexcept { return *_ecv_not_null(mainSharedSpiDevice); }

	static const char* _ecv_array GetResetReasonText() noexcept;
	static bool WasDeliberateError() noexcept { return deliberateError; }

	void LogError(ErrorCode e) noexcept { m_errorCodeBits |= (uint32_t)e; }

	[[nodiscard]] BoardType GetBoardType() const noexcept { return m_board; }
	void SetBoardType() noexcept;
	[[nodiscard]] const char* _ecv_array GetElectronicsString() const noexcept;
	[[nodiscard]] const char* _ecv_array GetBoardString() const noexcept;

	[[nodiscard]] size_t GetNumGpInputsToReport() const noexcept;
	[[nodiscard]] size_t GetNumGpOutputsToReport() const noexcept;

#if defined(DUET_NG) || defined(DUET3MINI)
	bool IsDuetWiFi() const noexcept;
	bool HasESP32() const noexcept;
#endif

#if HAS_WIFI_NETWORKING
	static const char* _ecv_array GetDefaultWiFiFirmwareName() noexcept;
#endif

#ifdef DUET_NG
	const char* _ecv_array GetBoardName() const noexcept;
	const char* _ecv_array GetBoardShortName() const noexcept;

	const float GetDefaultThermistorSeriesR(size_t inputNumber) const noexcept
	{
		// This is only called from one place so we may as well inline it
		return (inputNumber >= 3 && (expansionBoard == ExpansionBoardType::DueX5_v0_11 ||
									 expansionBoard == ExpansionBoardType::DueX2_v0_11))
				   ? DefaultThermistorSeriesR_DueX_v0_11
				   : DefaultThermistorSeriesR;
	}
#endif

	// Timing
	void Tick() noexcept SPEED_CRITICAL; // Process a systick interrupt

	// Real-time clock
	[[nodiscard]] bool IsDateTimeSet() const noexcept { return m_realTime != 0; } // Has the RTC been set yet?
	[[nodiscard]] time_t GetDateTime() const noexcept { return m_realTime; }	  // Retrieves the current RTC datetime
	bool GetDateTime(tm& rslt) const noexcept { return gmtime_r(&m_realTime, &rslt) != nullptr && m_realTime != 0; }
	// Retrieves the broken-down current RTC datetime and returns true if it's valid
	bool SetDateTime(time_t t) noexcept; // Sets the current RTC date and time or returns false on error

	// Message output (see MessageType for further details)
	void Message(MessageType type, const char* _ecv_array message) noexcept;
	static void Message(MessageType type, OutputBuffer* buffer) noexcept;
	void MessageF(MessageType type, const char* _ecv_array fmt, ...) noexcept __attribute__((format(printf, 3, 4)));
	void MessageV(MessageType type, const char* _ecv_array fmt, va_list vargs) noexcept;
	void DebugMessage(const char* _ecv_array fmt, va_list vargs) noexcept;
	bool FlushMessages() noexcept; // Flush messages to USB and aux, returning true if there is more to send

	// Movement
	void EmergencyStop() noexcept;

	// MCU temperature
#if HAS_CPU_TEMP_SENSOR
	[[nodiscard]] MinCurMax GetMcuTemperatures() const noexcept;
	void SetMcuTemperatureAdjust(float v) noexcept { m_mcuTemperatureAdjust = v; }
	[[nodiscard]] float GetMcuTemperatureAdjust() const noexcept { return m_mcuTemperatureAdjust; }
#endif

#if HAS_VOLTAGE_MONITOR
	// Power in voltage
	[[nodiscard]] MinCurMax GetPowerVoltages() const noexcept;
	[[nodiscard]] float GetCurrentPowerVoltage() const noexcept;
	[[nodiscard]] bool IsPowerOk() const noexcept;
	void DisableAutoSave() noexcept;
	void EnableAutoSave(float saveVoltage, float resumeVoltage) noexcept;
	bool GetAutoSaveSettings(float& saveVoltage, float& resumeVoltage) const noexcept;
#endif

#if HAS_12V_MONITOR
	// 12V rail voltage
	[[nodiscard]] MinCurMax GetV12Voltages() const noexcept;
	[[nodiscard]] float GetCurrentV12Voltage() const noexcept;
#endif

#if HAS_VOLTAGE_MONITOR || HAS_12V_MONITOR
	void ResetVoltageMonitors() noexcept;
	[[nodiscard]] float GetVinVoltage() const noexcept;
#else
	void ResetVoltageMonitors() noexcept {}
	bool HasDriverPower() const noexcept { return true; }
#endif

#if MCU_HAS_UNIQUE_ID
	[[nodiscard]] const UniqueId& GetUniqueId() const noexcept { return m_uniqueId; }
	uint32_t Random() noexcept;
#endif

#if SUPPORT_CAN_EXPANSION
	void HandleRemoteGpInChange(CanAddress src, uint8_t handleMajor, uint8_t handleMinor, bool state) noexcept;
#endif

#if SUPPORT_CAN_EXPANSION
	void OnProcessingCanMessage() noexcept; // called when we start processing any CAN message except for regular
											// messages e.g. time sync
#endif

#if defined(DUET3_MB6HC)
	static BoardType GetMB6HCBoardType() noexcept; // this is safe to call before Platform has been created
#endif
#if defined(DUET3_MB6XD)
	static BoardType GetMB6XDBoardType() noexcept; // this is safe to call before Platform has been created
#endif

	void SetDiagLed(bool on) const noexcept;

#if SUPPORT_MULTICAST_DISCOVERY
	void InvertDiagLed() const noexcept;
#endif

#if defined(DUET3MINI) && SUPPORT_TMC2240 != 0
	bool HasTmc2240Expansion() const noexcept { return hasTmc2240Expansion; }
	const char* _ecv_array null GetExpansionBoardName() const noexcept
	{
		return (hasTmc2240Expansion) ? "Duet3 Mini 2+ (TMC2240)" : nullptr;
	}
#endif

	// Debug buffer for M111
	static bool HasDebugBuffer() noexcept;
	static bool IsrDebugPutc(char c) noexcept;
	static bool SetDebugBufferSize(uint32_t size) noexcept;

  private:
	static SharedSpiDevice* _ecv_null mainSharedSpiDevice;

	void RawMessage(MessageType type,
					const char* _ecv_array message) noexcept; // called by Message after handling error/warning flags
	[[nodiscard]] float GetCpuTemperature() const noexcept;

#if defined(DUET3_MB6HC)
	[[nodiscard]] float AdcReadingToPowerVoltage(uint16_t adcVal) const noexcept;
	[[nodiscard]] uint16_t PowerVoltageToAdcReading(float voltage) const noexcept;
#endif

	// Board and processor
#if MCU_HAS_UNIQUE_ID
	UniqueId m_uniqueId;
#endif

	BoardType m_board;

	bool m_active;
	uint32_t m_errorCodeBits;

	void InitialiseInterrupts() noexcept;

	// Thermistors and temperature monitoring
	volatile ThermistorAveragingFilter m_adcFilters[NumAdcFilters]; // ADC reading averaging filters

#if HAS_CPU_TEMP_SENSOR
	float m_highestMcuTemperature{}, m_lowestMcuTemperature{};
	float m_mcuTemperatureAdjust{};
#  if SAME5x
	TempSenseAveragingFilter tpFilter, tcFilter;
	int32_t tempCalF1, tempCalF2, tempCalF3, tempCalF4; // temperature calibration factors
	void TemperatureCalibrationInit() noexcept;
#  endif
#endif

	// Data used by the tick interrupt handler
	AnalogChannelNumber m_filteredAdcChannels[NumAdcFilters]{};
	AnalogChannelNumber m_zProbeAdcChannel{};
	uint8_t m_tickState;
	size_t m_currentFilterNumber{};
	unsigned int m_debugCode;

	// Hotend configuration
	float m_filamentWidth{};

	// Power monitoring
#if HAS_VOLTAGE_MONITOR
	AnalogChannelNumber m_vInMonitorAdcChannel{};
	volatile uint16_t m_currentVin{}, m_highestVin{}, m_lowestVin{};
	uint16_t m_lastVinUnderVoltageValue{}, m_lastVinOverVoltageValue{};
	uint16_t m_autoPauseReading{}, m_autoResumeReading{};
	std::atomic<uint32_t> m_numVinUnderVoltageEvents, m_numVinOverVoltageEvents;
	uint32_t m_previousVinUnderVoltageEvents{}, m_previousVinOverVoltageEvents{};

#  ifdef DUET3_MB6HC
	float m_powerMonitorVoltageRange{};
	uint16_t m_driverPowerOnAdcReading{};
	uint16_t m_driverPowerOffAdcReading{};
	Pin DiagPin{};
	Pin ActLedPin{};
	bool DiagOnPolarity{};
#  endif

	bool m_autoSaveEnabled{};

	enum class AutoSaveState : uint8_t
	{
		Starting = 0,
		Normal,
		AutoPaused
	};
	AutoSaveState m_autoSaveState{};
#endif

#if HAS_12V_MONITOR
	AnalogChannelNumber m_v12MonitorAdcChannel{};
	volatile uint16_t m_currentV12{}, m_highestV12{}, m_lowestV12{};
	uint16_t m_lastV12UnderVoltageValue{};
	std::atomic<uint32_t> m_numV12UnderVoltageEvents;
	uint32_t m_previousV12UnderVoltageEvents{};
#endif

	// Event handling
	uint32_t m_lastDriverPollMillis; // when we last checked the drivers and voltage monitoring

#if SUPPORT_CAN_EXPANSION
	uint32_t m_whenLastCanMessageProcessed;
#endif

	// RTC
	time_t m_realTime{};				// the current date/time, or zero if never set
	uint32_t m_timeLastUpdatedMillis{}; // the milliseconds counter when we last incremented the time

	// Misc
	static bool deliberateError; // true if we deliberately caused an exception for testing purposes. Must be static in
								 // case of exception during startup.
};

//*****************************************************************************************************************

#endif
