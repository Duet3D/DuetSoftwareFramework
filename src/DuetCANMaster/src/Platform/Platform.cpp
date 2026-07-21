/****************************************************************************************************

 RepRapFirmware

 Platform contains all the code and definitions to deal with machine-dependent things such as control
 pins, bed area, number of extruders, tolerable accelerations and speeds and so on.

 -----------------------------------------------------------------------------------------------------

 Version 0.1

 18 November 2012

 Adrian Bowyer
 RepRap Professional Ltd
 http://reprappro.com

 Licence: GPL

 ****************************************************************************************************/

#include "Platform.h"

#include <Devices.h>
#include <Movement/StepTimer.h>

#include "RepRap.h"

#include "Event.h"
#include <Version.h>

#include "Tasks.h"
#include <Cache.h>
#include <SPI/SharedSpiDevice.h>

#include <Math/Isqrt.h>

#include <Hardware/NonVolatileMemory.h>
#include <Storage/CRC32.h>

#if NUM_ASYNC_PORTS != 0
#  include <AsyncSerial.h>
#endif

#if SAM4E || SAM4S || SAME70
#  include <AnalogIn.h>
#  include <DmacManager.h>
#  include <pmc/pmc.h>
#  if SAME70
static_assert(NumDmaChannelsUsed <= NumDmaChannelsSupported, "Need more DMA channels in CoreNG");
#  endif
#elif SAME5x
#  include <AnalogIn.h>
#  include <DmacManager.h>
#endif

#if HAS_SBC_INTERFACE
#  include "SBC/SbcInterface.h"

#  include "SBC/DataTransfer.h"
#endif

#if SUPPORT_CAN_EXPANSION
#  include "CAN/CanMessageGenericConstructor.h"

#  include "CAN/CanInterface.h"
#  include <CanMessageGenericTables.h>
#endif

#include <climits>

#if !defined(HAS_LWIP_NETWORKING) || !defined(HAS_WIFI_NETWORKING) || !defined(HAS_CPU_TEMP_SENSOR) ||                 \
	!defined(HAS_HIGH_SPEED_SD) || !defined(HAS_SMART_DRIVERS) || !defined(HAS_STALL_DETECT) ||                        \
	!defined(HAS_VOLTAGE_MONITOR) || !defined(HAS_12V_MONITOR) || !defined(HAS_VREF_MONITOR) ||                        \
	!defined(SUPPORT_NONLINEAR_EXTRUSION) || !defined(SUPPORT_ASYNC_MOVES) || !defined(HAS_MASS_STORAGE) ||            \
	!defined(HAS_EMBEDDED_FILES)
#  error Missing feature definition
#endif

#if HAS_VOLTAGE_MONITOR

#  if defined(DUET3_MB6HC)

float Platform::AdcReadingToPowerVoltage(uint16_t adcVal) const noexcept
{
	return (adcVal * powerMonitorVoltageRange) / (1u << AnalogIn::AdcBits);
}

uint16_t Platform::PowerVoltageToAdcReading(float voltage) const noexcept
{
	return (uint16_t)((voltage * (1u << AnalogIn::AdcBits)) / powerMonitorVoltageRange);
}

#  else

inline constexpr float AdcReadingToPowerVoltage(uint16_t adcVal) noexcept
{
	return adcVal * (PowerMonitorVoltageRange / (1u << AnalogIn::AdcBits));
}

inline constexpr uint16_t PowerVoltageToAdcReading(float voltage) noexcept
{
	return (uint16_t)(voltage * ((1u << AnalogIn::AdcBits) / PowerMonitorVoltageRange));
}

constexpr uint16_t driverPowerOnAdcReading =
	PowerVoltageToAdcReading(10.0); // minimum voltage at which we initialise the drivers
constexpr uint16_t driverPowerOffAdcReading =
	PowerVoltageToAdcReading(9.5); // voltages below this flag the drivers as unusable

#  endif

#  if ENFORCE_MAX_VIN
constexpr uint16_t driverOverVoltageAdcReading =
	PowerVoltageToAdcReading(29.0); // voltages above this cause driver shutdown
constexpr uint16_t driverNormalVoltageAdcReading =
	PowerVoltageToAdcReading(27.5); // voltages at or below this are normal
#  endif

#endif

#if HAS_12V_MONITOR

constexpr float AdcReadingToV12Voltage(uint16_t adcVal) noexcept
{
	return adcVal * (V12MonitorVoltageRange / (1u << AnalogIn::AdcBits));
}

constexpr uint16_t V12VoltageToAdcReading(float voltage) noexcept
{
	return (uint16_t)(voltage * ((1u << AnalogIn::AdcBits) / V12MonitorVoltageRange));
}

constexpr uint16_t driverV12OnAdcReading =
	V12VoltageToAdcReading(10.0); // minimum voltage at which we initialise the drivers
constexpr uint16_t driverV12OffAdcReading =
	V12VoltageToAdcReading(9.5); // voltages below this flag the drivers as unusable

#endif

// Global variable for debugging in tricky situations e.g. within ISRs
int debugLine = 0;

// Global functions

//*************************************************************************************************
// Platform class

bool Platform::deliberateError = false; // true if we deliberately caused an exception for testing purposes
SharedSpiDevice* _ecv_null Platform::mainSharedSpiDevice = nullptr;

Platform::Platform() noexcept
	: board(DEFAULT_BOARD_TYPE)
	, active(false)
	, errorCodeBits(0)
	, tickState(0)
	, debugCode(0)
	, lastDriverPollMillis(0)
	,
#if SUPPORT_CAN_EXPANSION
	whenLastCanMessageProcessed(0)
#endif
{
}

static RingBuffer<char> isrDebugBuffer;

// Return true if we have a debug buffer
bool Platform::HasDebugBuffer() noexcept
{
	return isrDebugBuffer.GetCapacity() != 0;
}

// Write a character to the debug buffer
bool Platform::IsrDebugPutc(char c) noexcept
{
	if (c != 0)
	{
		const bool b = isrDebugBuffer.PutItem(c);
		return b;
	}

	return true;
}

// Set the size of the debug buffer returning true if successful
bool Platform::SetDebugBufferSize(uint32_t size) noexcept
{
	if ((size & (size - 1)) == 0)
	{
		isrDebugBuffer.Init(size);
		return true;
	}
	return false;
}

// Initialise the Platform. Note: this is the first module to be initialised, so don't call other modules from here!
void Platform::Init() noexcept
{
#if HAS_LWIP_NETWORKING
	SetPinMode(EthernetPhyResetPin, OUTPUT_LOW); // reset the Ethernet Phy chip
#endif

	// Do any board-specific initialisation that needs to be done early and does not depend on the board revision

#if HAS_SMART_DRIVERS
	// Make sure the on-board drivers are disabled
	SetPinMode(GlobalTmcEnablePin, OUTPUT_HIGH);
#endif

	// Make sure any WiFi module is held in reset
#if defined(DUET_NG)
	SetPinMode(EspResetPin, OUTPUT_LOW); // reset the WiFi module or the W5500
	SetPinMode(EspEnablePin, OUTPUT_LOW);
#elif defined(DUET3_MB6HC)
	SetPinMode(EspEnablePin, OUTPUT_LOW); // make sure that the Wifi module if present is disabled
#endif

	// Set up the local drivers. Do this after we have read any direction pins that specify the board type.
#if defined(DUET3MINI) && SUPPORT_TMC2240
	// Check whether we have a TMC2240 prototype expansion board connected, before we set the driver direction pins to
	// outputs
	SetPinMode(DIRECTION_PINS[5], INPUT_PULLUP, false);
	delayMicroseconds(20); // give the pullup resistor time to work
	hasTmc2240Expansion = !digitalRead(DIRECTION_PINS[5]);
#endif

	// Sort out which board we are running on (some firmware builds support more than one board variant)
	SetBoardType();

#if MCU_HAS_UNIQUE_ID
	uniqueId.SetFromCurrentBoard();
#endif

	// Real-time clock
	realTime = 0;

	// Turn off the RS485 transmitter
#if defined(DUET3_MB6XD)
	SetPinMode(ModbusTxPin, OUTPUT_LOW);
#elif defined(DUET3_MB6HC)
	if (board == BoardType::Duet3_6HC_v102c)
	{
		SetPinMode(ModbusTxPin, OUTPUT_LOW);
	}
#endif

	// Initialise the IO port subsystem
	IoPort::Init();

	// Shared SPI subsystem
	mainSharedSpiDevice = new SharedSpiDevice(SharedSpiParams);

#if HAS_MASS_STORAGE
	// File management and SD card interfaces
	for (const unsigned char sdCardDetectPin : SdCardDetectPins)
	{
		SetPinMode(sdCardDetectPin, INPUT_PULLUP, true);
	}
#endif

#if HAS_VOLTAGE_MONITOR
	autoSaveEnabled = false;
	autoSaveState = AutoSaveState::starting;
#endif

#if HAS_VREF_MONITOR
	// Set up the VSSA and VREF measurement channels
	SetPinMode(VssaSensePin, AIN);
	filteredAdcChannels[VssaFilterIndex] =
		PinToAdcChannel(VssaSensePin); // translate the pin number to the SAM ADC channel number
	SetPinMode(VrefSensePin, AIN);
	filteredAdcChannels[VrefFilterIndex] =
		PinToAdcChannel(VrefSensePin); // translate the pin number to the SAM ADC channel number
#endif

#if HAS_CPU_TEMP_SENSOR
#  if SAME5x
	tpFilter.Init(0);
	AnalogIn::EnableTemperatureSensor(0, tpFilter.CallbackFeedIntoFilter, CallbackParameter(&tpFilter), 1, 0);
	tcFilter.Init(0);
	AnalogIn::EnableTemperatureSensor(1, tcFilter.CallbackFeedIntoFilter, CallbackParameter(&tcFilter), 1, 0);
	TemperatureCalibrationInit();
#  else
	filteredAdcChannels[CpuTempFilterIndex] =
#	if SAM4E || SAM4S || SAME70
		LegacyAnalogIn::
#	endif
			GetTemperatureAdcChannel();
#  endif
#endif

	// Initialise all the ADC filters and enable the corresponding ADC channels
	for (size_t filter = 0; filter < NumAdcFilters; ++filter)
	{
		adcFilters[filter].Init(0);
		AnalogInEnableChannel(filteredAdcChannels[filter], true);
	}

	// Hotend configuration
	filamentWidth = FILAMENT_WIDTH;

#if HAS_CPU_TEMP_SENSOR
	// MCU temperature monitoring
	highestMcuTemperature = -273.0; // the highest temperature we have seen
	lowestMcuTemperature = 2000.0;	// the lowest temperature we have seen
	mcuTemperatureAdjust = 0.0;
#endif

#if HAS_VOLTAGE_MONITOR
	// Power monitoring
	vInMonitorAdcChannel = PinToAdcChannel(PowerMonitorVinDetectPin);
	SetPinMode(PowerMonitorVinDetectPin, AIN);
	AnalogInEnableChannel(vInMonitorAdcChannel, true);
	currentVin = 0;
	highestVin = 0;
	lowestVin = 9999;
	numVinUnderVoltageEvents = 0;
	previousVinUnderVoltageEvents = 0;
	numVinOverVoltageEvents = 0;
	previousVinOverVoltageEvents = 0;
#endif

#if HAS_12V_MONITOR
	// Power monitoring
	v12MonitorAdcChannel = PinToAdcChannel(PowerMonitorV12DetectPin);
	SetPinMode(PowerMonitorV12DetectPin, AIN);
	AnalogInEnableChannel(v12MonitorAdcChannel, true);
	currentV12 = 0;
	highestV12 = 0;
	lowestV12 = 9999;
	numV12UnderVoltageEvents = previousV12UnderVoltageEvents = 0;
#endif

	// Kick everything off
	InitialiseInterrupts();

#ifdef DUET_NG
	DuetExpansion::DueXnTaskInit(); // must initialise interrupt priorities before calling this
#endif
	active = true;
}

#if HAS_VOLTAGE_MONITOR

// Reset the min and max recorded voltages to the current values
void Platform::ResetVoltageMonitors() noexcept
{
	lowestVin = currentVin;
	highestVin = currentVin;

#  if HAS_12V_MONITOR
	lowestV12 = currentV12;
	highestV12 = currentV12;
#  endif
}

float Platform::GetVinVoltage() const noexcept
{
	return AdcReadingToPowerVoltage(currentVin);
}

#endif

void Platform::Exit() noexcept
{
	// Stop processing data. Don't try to send a message because it will probably never get there.
	active = false;
}

void Platform::Spin() noexcept
{
	if (!active)
	{
		return;
	}

#if SUPPORT_CAN_EXPANSION
	// Turn off the ACT LED if it is time to do so
	if (millis() - whenLastCanMessageProcessed > ActLedFlashTime)
	{
		digitalWrite(ActLedPin, !ActOnPolarity);
	}
#endif

	// Check for M111 debug messages stored in the optional buffer
	while (!isrDebugBuffer.IsEmpty())
	{
		char buf[101];
		const unsigned int charsRead = isrDebugBuffer.GetBlock(buf, sizeof(buf) - 1);
		buf[charsRead] = 0;
		Message(UsbMessage, buf);
	}

	// Check the MCU max and min temperatures
#if HAS_CPU_TEMP_SENSOR
#  if SAME5x
	if (tcFilter.IsValid() && tpFilter.IsValid())
#  else
	if (adcFilters[CpuTempFilterIndex].IsValid())
#  endif
	{
		const float currentMcuTemperature = GetCpuTemperature();
		if (currentMcuTemperature > highestMcuTemperature)
		{
			highestMcuTemperature = currentMcuTemperature;
		}
		if (currentMcuTemperature < lowestMcuTemperature)
		{
			lowestMcuTemperature = currentMcuTemperature;
		}
	}
#endif

	// TODO low voltage check. We may want to automatically send some CAN messages to stop motion or disable heaters?
	// Can this be handled by the SBC?

	// Diagnostics test
	if (debugCode == (unsigned int)DiagnosticTestType::TestSpinLockup)
	{
		delay(30000);
	}

	const uint32_t now = millis();

	// Update the time
	if (IsDateTimeSet() && now - timeLastUpdatedMillis >= 1000)
	{
		++realTime; // this assumes that time_t is a seconds-since-epoch counter, which is not guaranteed by the C
					// standard
		timeLastUpdatedMillis += 1000;
	}
}

#if HAS_VOLTAGE_MONITOR

void Platform::DisableAutoSave() noexcept
{
	autoSaveEnabled = false;
}

bool Platform::IsPowerOk() const noexcept
{
	return !autoSaveEnabled || currentVin > autoPauseReading;
}

void Platform::EnableAutoSave(float saveVoltage, float resumeVoltage) noexcept
{
	autoPauseReading = PowerVoltageToAdcReading(saveVoltage);
	autoResumeReading = PowerVoltageToAdcReading(resumeVoltage);
	autoSaveEnabled = true;
}

bool Platform::GetAutoSaveSettings(float& saveVoltage, float& resumeVoltage) noexcept
{
	if (autoSaveEnabled)
	{
		saveVoltage = AdcReadingToPowerVoltage(autoPauseReading);
		resumeVoltage = AdcReadingToPowerVoltage(autoResumeReading);
	}
	return autoSaveEnabled;
}

#endif

#if HAS_CPU_TEMP_SENSOR

float Platform::GetCpuTemperature() const noexcept
{
#  if SAME5x
	// From the datasheet:
	// T = (tl * vph * tc - th * vph * tc - tl * tp *vch + th * tp * vcl)/(tp * vcl - tp * vch - tc * vpl * tc * vph)
	const uint16_t tc_result = tcFilter.GetSum() / (tcFilter.NumAveraged() << (AnalogIn::AdcBits - 12));
	const uint16_t tp_result = tpFilter.GetSum() / (tpFilter.NumAveraged() << (AnalogIn::AdcBits - 12));

	int32_t result = (tempCalF1 * tc_result - tempCalF2 * tp_result);
	const int32_t divisor = (tempCalF3 * tp_result - tempCalF4 * tc_result);
	result = (divisor == 0) ? 0 : result / divisor;
	return (float)result / 16 + mcuTemperatureAdjust;
#  else
	const float voltage = (float)adcFilters[CpuTempFilterIndex].GetSum() *
						  (3.3 / (float)((1u << AnalogIn::AdcBits) * ThermistorAverageReadings));
#	if SAM4E || SAM4S
	return (voltage - 1.44) * (1000.0 / 4.7) + 27.0 + mcuTemperatureAdjust; // accuracy at 27C is +/-13C
#	elif SAME70
	return (voltage - 0.72) * (1000.0 / 2.33) + 25.0 + mcuTemperatureAdjust; // accuracy at 25C is +/-34C
#	else
#	  error undefined CPU temp conversion
#	endif
#  endif
}

#endif

//*****************************************************************************************************************
// Interrupts

#if SAME5x
// Set a contiguous range of interrupts to the specified priority
static void SetInterruptPriority(IRQn base, unsigned int num, uint32_t prio)
{
	do
	{
		NVIC_SetPriority(base, prio);
		base = (IRQn)(base + 1);
		--num;
	} while (num != 0);
}
#endif

void Platform::InitialiseInterrupts() noexcept
{
	// Watchdog interrupt priority if applicable has already been set up in RepRap::Init

#if HAS_HIGH_SPEED_SD
	NVIC_SetPriority(SdhcIRQn, NvicPriorityHSMCI); // set priority for SD interface interrupts
#endif

	// Set PanelDue UART interrupt priority is set in AuxDevice::Init
	// WiFi UART interrupt priority is now set in module WiFiInterface

#if SUPPORT_TMC22xx && !SAME5x // SAME5x uses a DMA interrupt instead of the UART interrupt
#  if TMC22xx_HAS_MUX
	NVIC_SetPriority(TMC22xx_UART_IRQn, NvicPriorityDriversSerialTMC); // set priority for TMC2660 SPI interrupt
#  else
	NVIC_SetPriority(TMC22xxUartIRQns[0], NvicPriorityDriversSerialTMC);
	NVIC_SetPriority(TMC22xxUartIRQns[1], NvicPriorityDriversSerialTMC);
#  endif
#endif

#if SUPPORT_TMC2660
	NVIC_SetPriority(TMC2660_SPI_IRQn, NvicPriorityDriversSerialTMC); // set priority for TMC2660 SPI interrupt
#endif

#if HAS_LWIP_NETWORKING
	// Set up the Ethernet interface priority here to because we have access to the priority definitions
#  if SAME70 || SAME5x
	NVIC_SetPriority(GMAC_IRQn, NvicPriorityEthernet);
#  else
	NVIC_SetPriority(EMAC_IRQn, NvicPriorityEthernet);
#  endif
#endif

#if SAME5x
	SetInterruptPriority(DMAC_0_IRQn, 5, NvicPriorityDMA); // SAME5x DMAC has 5 contiguous IRQ numbers
#elif SAME70
	NVIC_SetPriority(XDMAC_IRQn, NvicPriorityDMA);
#endif

#if SAME5x
	SetInterruptPriority(EIC_0_IRQn, 16, NvicPriorityPins); // SAME5x EXINT has 16 contiguous IRQ numbers
#else
	NVIC_SetPriority(PIOA_IRQn, NvicPriorityPins);
	NVIC_SetPriority(PIOB_IRQn, NvicPriorityPins);
	NVIC_SetPriority(PIOC_IRQn, NvicPriorityPins);
#  ifdef ID_PIOD
	NVIC_SetPriority(PIOD_IRQn, NvicPriorityPins);
#  endif
#  ifdef ID_PIOE
	NVIC_SetPriority(PIOE_IRQn, NvicPriorityPins);
#  endif
#endif

#if SAME5x
	SetInterruptPriority(USB_0_IRQn, 4, NvicPriorityUSB); // SAME5x USB has 4 contiguous IRQ numbers
#elif SAME70
	NVIC_SetPriority(USBHS_IRQn, NvicPriorityUSB);
#elif SAM4E || SAM4S
	NVIC_SetPriority(UDP_IRQn, NvicPriorityUSB);
#elif SAM3XA
	NVIC_SetPriority(UOTGHS_IRQn, NvicPriorityUSB);
#else
#  error Unsupported processor
#endif

#if defined(DUET_NG) || defined(DUET_M)
	NVIC_SetPriority(I2C_IRQn, NvicPriorityTwi);
#endif

#if SUPPORT_CAN_EXPANSION
#  if SAME5x
	NVIC_SetPriority(CAN0_IRQn, NvicPriorityCan);
	NVIC_SetPriority(CAN1_IRQn, NvicPriorityCan);
#  elif SAME70
	NVIC_SetPriority(MCAN0_INT0_IRQn, NvicPriorityCan); // we don't use INT1
	NVIC_SetPriority(MCAN1_INT0_IRQn, NvicPriorityCan); // we don't use INT1
#  endif
#endif

	// Tick interrupt for ADC conversions
	tickState = 0;
	currentFilterNumber = 0;
}

//*************************************************************************************************

// Debugging variables
// extern "C" uint32_t longestWriteWaitTime, shortestWriteWaitTime, longestReadWaitTime, shortestReadWaitTime;
// extern uint32_t maxRead, maxWrite;

/*static*/ const char* _ecv_array Platform::GetResetReasonText() noexcept
{
#if SAME5x
	const uint8_t resetReason = RSTC->RCAUSE.reg;
	// The datasheet says only one of these bits will be set
	if (resetReason & RSTC_RCAUSE_POR)
	{
		return "power up";
	}
	if (resetReason & RSTC_RCAUSE_BODCORE)
	{
		return "core brownout";
	}
	if (resetReason & RSTC_RCAUSE_BODVDD)
	{
		return "Vdd brownout";
	}
	if (resetReason & RSTC_RCAUSE_WDT)
	{
		return "watchdog";
	}
	if (resetReason & RSTC_RCAUSE_NVM)
	{
		return "NVM";
	}
	if (resetReason & RSTC_RCAUSE_EXT)
	{
		return "reset button";
	}
	if (resetReason & RSTC_RCAUSE_SYST)
	{
		return "software";
	}
	if (resetReason & RSTC_RCAUSE_BACKUP)
	{
		return "backup/hibernate";
	}
	return "unknown";
#else
	constexpr const char* _ecv_array resetReasons[8] = {
		"power up",
		"backup",
		"watchdog",
		"software",
#  ifdef DUET_NG
		// On the SAM4E a watchdog reset may be reported as a user reset because of the capacitor on the NRST pin.
		// The SAM4S is the same but the Duet Maestro has a diode in the reset circuit to avoid this problem.
		"reset button or watchdog",
#  else
		"reset button",
#  endif
		"unknown",
		"unknown",
		"unknown"};
	return resetReasons[(REG_RSTC_SR & RSTC_SR_RSTTYP_Msk) >> RSTC_SR_RSTTYP_Pos];
#endif
}

// Return diagnostic information. Each part must fit in a buffer of length GCodeReplyLength.
void Platform::Diagnostics(unsigned int part, const StringRef& reply) noexcept
{
	switch (part)
	{
	case 0:
		reply.copy("=== Platform ===");

		// Debugging support
		if (debugLine != 0)
		{
			reply.lcatf("Debug line %d", debugLine);
		}

		// Show the up time and reason for the last reset
		{
			const auto now = (uint32_t)(millis64() / 1000u); // get up time in seconds
			reply.lcatf("Last reset %02d:%02d:%02d ago, cause: %s",
						(unsigned int)(now / 3600),
						(unsigned int)((now % 3600) / 60),
						(unsigned int)(now % 60),
						GetResetReasonText());
		}
		break;

	case 1:
		// Show the reset code stored at the last software reset
		{
			NonVolatileMemory mem;
			unsigned int slot = 0;
			const SoftwareResetData* const _ecv_null srd = mem.GetLastWrittenResetData(slot);
			if (srd == nullptr)
			{
				reply.lcat("Last software reset details not available");
			}
			else
			{
				srd->PrintPart1(slot, reply);
			}
		}
		break;

	case 2:
	{
		// Show the reset code stored at the last software reset
		NonVolatileMemory mem;
		unsigned int slot = 0;
		const SoftwareResetData* const _ecv_null srd = mem.GetLastWrittenResetData(slot);
		if (srd != nullptr)
		{
			srd->PrintPart2(reply);
		}
	}
	break;

	case 3:
		// Show the current error codes
		reply.printf("Error status: 0x%02" PRIx32,
					 errorCodeBits); // we only use the bottom 5 bits at present, so print just 2 characters
		break;

	case 4:
#if HAS_CPU_TEMP_SENSOR
		// Show the MCU temperatures
		{
			const float currentMcuTemperature = GetCpuTemperature();
			reply.lcatf("MCU temperature: min %.1f, current %.1f, max %.1f",
						(double)lowestMcuTemperature,
						(double)currentMcuTemperature,
						(double)highestMcuTemperature);
			lowestMcuTemperature = highestMcuTemperature = currentMcuTemperature;
#  if HAS_VOLTAGE_MONITOR
			// No need to call reprap.BoardsUpdated() here because that is done in ResetVoltageMonitors which is called
			// later
#  else
			reprap.BoardsUpdated();
#  endif
		}
#endif

#if HAS_VOLTAGE_MONITOR
		// Show the supply voltage
		reply.lcatf("Supply voltage: min %.1f, current %.1f, max %.1f, under voltage events: %" PRIu32
					", over voltage events: %" PRIu32 "",
					(double)AdcReadingToPowerVoltage(lowestVin),
					(double)AdcReadingToPowerVoltage(currentVin),
					(double)AdcReadingToPowerVoltage(highestVin),
					numVinUnderVoltageEvents.load(),
					numVinOverVoltageEvents.load());
#endif

#if HAS_12V_MONITOR
		// Show the 12V rail voltage
		reply.lcatf("12V rail voltage: min %.1f, current %.1f, max %.1f, under voltage events: %" PRIu32,
					(double)AdcReadingToV12Voltage(lowestV12),
					(double)AdcReadingToV12Voltage(currentV12),
					(double)AdcReadingToV12Voltage(highestV12),
					numV12UnderVoltageEvents.load());
#endif
		ResetVoltageMonitors();
		break;

	case 5:
		Event::Diagnostics(reply, *this);
		break;

	case 6:
		// Show current RTC time
		{
			reply.lcat("Date/time: ");
			struct tm timeInfo
			{
			};
			if (gmtime_r(&realTime, &timeInfo) != nullptr)
			{
				reply.catf("%04u-%02u-%02u %02u:%02u:%02u",
						   timeInfo.tm_year + 1900,
						   timeInfo.tm_mon + 1,
						   timeInfo.tm_mday,
						   timeInfo.tm_hour,
						   timeInfo.tm_min,
						   timeInfo.tm_sec);
			}
			else
			{
				reply.cat("not set");
			}
		}
		reprap.Timing(reply);

#ifdef I2C_IFACE
		{
			const TwoWire::ErrorCounts errs = I2C_IFACE.GetErrorCounts(true);
			reply.lcatf("I2C nak errors %" PRIu32 ", send timeouts %" PRIu32 ", receive timeouts %" PRIu32
						", finishTimeouts %" PRIu32 ", resets %" PRIu32,
						errs.naks,
						errs.sendTimeouts,
						errs.recvTimeouts,
						errs.finishTimeouts,
						errs.resets);
		}
#endif
		break;

		static_assert(NumPlatformDiagnosticParts == 7);

	default:
		// 'part' is validated by the caller against NumPlatformDiagnosticParts
		break;
	}

#if CORE_USES_TINYUSB // DEBUG
//	MessageF(mtype, "USB interrupts %" PRIu32 "\n", numUsbInterrupts);
#endif

#if 0
	// Debugging temperature readings
	const uint32_t div = ThermistorAveragingFilter::NumAveraged() >> 2;		// 2 oversample bits
	MessageF(mtype, "Vssa %" PRIu32 " Vref %" PRIu32 " Temp0 %" PRIu32 " Temp1 %" PRIu32 "\n",
			adcFilters[VssaFilterIndex].GetSum()/div, adcFilters[VrefFilterIndex].GetSum()/div, adcFilters[0].GetSum()/div, adcFilters[1].GetSum()/div);
#endif

#ifdef SOFT_TIMER_DEBUG
	MessageF(mtype,
			 "Soft timer interrupts executed %u, next %u scheduled at %u, now %u\n",
			 numSoftTimerInterruptsExecuted,
			 STEP_TC->TC_CHANNEL[STEP_TC_CHAN].TC_RB,
			 lastSoftTimerInterruptScheduledAt,
			 GetTimerTicks());
#endif
}

#if 0
// Execute a timed square root that takes less than one millisecond
static uint32_t TimedSqrt(uint64_t arg, uint32_t& timeAcc) noexcept
{
	IrqDisable();
	asm volatile("":::"memory");
	uint32_t now1 = SysTick->VAL;
	const uint32_t ret = isqrt64(arg);
	uint32_t now2 = SysTick->VAL;
	asm volatile("":::"memory");
	IrqEnable();
	now1 &= 0x00FFFFFF;
	now2 &= 0x00FFFFFF;
	timeAcc += ((now1 > now2) ? now1 : now1 + (SysTick->LOAD & 0x00FFFFFF) + 1) - now2;
	return ret;
}
#endif

//-----------------------------------------------------------------------------------------------------

// Send the specified message to the specified destinations. The Error and Warning flags have already been handled.
void Platform::RawMessage(MessageType type, const char* _ecv_array message) noexcept {}

// Note: this overload of Platform::Message does not process the special action flags in the MessageType.
// Also it treats calls to send a blocking USB message the same as ordinary USB messages,
// and calls to send an immediate LCD message the same as ordinary LCD messages
void Platform::Message(MessageType type, OutputBuffer* buffer) noexcept
{
	// Now send the message to all the destinations
	unsigned int numDestinations = 0;
	if ((type & (AuxMessage | ImmediateAuxMessage)) != 0)
	{
		++numDestinations;
	}
#if NUM_ASYNC_CHANNELS > 1
	if ((type & Aux2Message) != 0)
	{
		++numDestinations;
	}
#endif
	if ((type & (UsbMessage | BlockingUsbMessage)) != 0)
	{
		++numDestinations;
	}
#ifdef SERIAL_USB2_DEVICE
	if ((type & Usb2Message) != 0)
	{
		++numDestinations;
	}
#endif
	if ((type & HttpMessage) != 0)
	{
		++numDestinations;
	}
	if ((type & TelnetMessage) != 0)
	{
		++numDestinations;
	}
#if HAS_SBC_INTERFACE
	if (((type & GenericMessage) == GenericMessage || (type & BinaryCodeReplyFlag) != 0))
	{
		++numDestinations;
	}
#endif

	if (numDestinations == 0)
	{
		OutputBuffer::ReleaseAll(buffer);
	}
	else
	{
		buffer->IncreaseReferences(numDestinations - 1);

#if HAS_SBC_INTERFACE
		if (((type & GenericMessage) == GenericMessage || (type & BinaryCodeReplyFlag) != 0))
		{
			reprap.GetSbcInterface().HandleGCodeReply(type, buffer);
		}
#endif
	}
}

void Platform::MessageV(MessageType type, const char* _ecv_array fmt, va_list vargs) noexcept
{
	String<FormatStringLength> formatString;
#if HAS_SBC_INTERFACE
	if (((type & GenericMessage) == GenericMessage || (type & BinaryCodeReplyFlag) != 0))
	{
		formatString.vprintf(fmt, vargs);
		reprap.GetSbcInterface().HandleGCodeReply(type, formatString.c_str());
		if ((type & BinaryCodeReplyFlag) != 0)
		{
			return;
		}
	}
#endif

	if ((type & ErrorMessageFlag) != 0)
	{
		formatString.copy("Error: ");
		formatString.vcatf(fmt, vargs);
	}
	else if ((type & WarningMessageFlag) != 0)
	{
		formatString.copy("Warning: ");
		formatString.vcatf(fmt, vargs);
	}
	else
	{
		formatString.vprintf(fmt, vargs);
	}

	RawMessage((MessageType)(type & ~(ErrorMessageFlag | WarningMessageFlag)), formatString.c_str());
}

void Platform::MessageF(MessageType type, const char* _ecv_array fmt, ...) noexcept
{
	va_list vargs;
	va_start(vargs, fmt);
	MessageV(type, fmt, vargs);
	va_end(vargs);
}

// TODO make this send to SBC via SPI
void Platform::Message(MessageType type, const char* _ecv_array message) noexcept
{
#if HAS_SBC_INTERFACE
	if (((type & BinaryCodeReplyFlag) != 0 || (type & GenericMessage) == GenericMessage || (type & LogOff) != LogOff))
	{
		reprap.GetSbcInterface().HandleGCodeReply(type, message);
		if ((type & BinaryCodeReplyFlag) != 0)
		{
			return;
		}
	}
#endif

	if ((type & (ErrorMessageFlag | WarningMessageFlag)) == 0)
	{
		RawMessage(type, message);
	}
	else
	{
#ifdef DUET3_ATE
		// FormatStringLength is too short for some ATE replies
		OutputBuffer* buf;
		if (OutputBuffer::Allocate(buf))
		{
			buf->copy(((type & ErrorMessageFlag) != 0) ? "Error: " : "Warning: ");
			buf->cat(message);
			Message(type, buf);
		}
		else
#endif
		{
			String<FormatStringLength> formatString;
			formatString.copy(((type & ErrorMessageFlag) != 0) ? "Error: " : "Warning: ");
			formatString.cat(message);
			RawMessage((MessageType)(type & ~(ErrorMessageFlag | WarningMessageFlag)), formatString.c_str());
		}
	}
}

// Send a debug message
// TODO decide whether to send this to the SBC
void Platform::DebugMessage(const char* _ecv_array fmt, va_list vargs) noexcept {}

#if defined(DUET3_MB6HC)

// This is safe to call before Platform has been created
/*static*/ BoardType Platform::GetMB6HCBoardType() noexcept
{
	// Driver 0 direction has a pulldown resistor on v0.6 and v1.0 boards, but not on v1.01 or v1.02 boards
	// Driver 1 has a pulldown resistor on v0.1 and v1.0 boards, however we don't support v0.1 and we don't care about
	// the difference between v0.6 and v1.0, so we don't need to read it Driver 2 has a pulldown resistor on v1.10,
	// v1.02, 1.02a, 1.02b, 1.02c Driver 3 has a pulldown resistor on v1.02c
	SetPinMode(DIRECTION_PINS[2], INPUT_PULLUP, false);
	SetPinMode(DIRECTION_PINS[0], INPUT_PULLUP, false);
	delayMicroseconds(20); // give the pullup resistor time to work
	if (digitalRead(DIRECTION_PINS[2]))
	{
		return (digitalRead(DIRECTION_PINS[0])) ? BoardType::Duet3_6HC_v101 : BoardType::Duet3_6HC_v06_100;
	}
	else if (digitalRead(DIRECTION_PINS[0]))
	{
		return BoardType::Duet3_6HC_v102;
	}
	else
	{
		return (digitalRead(DIRECTION_PINS[3])) ? BoardType::Duet3_6HC_v102b : BoardType::Duet3_6HC_v102c;
	}
}

#endif

#if defined(DUET3_MB6XD)

// This is safe to call before Platform has been created
/*static*/ BoardType Platform::GetMB6XDBoardType() noexcept
{
	// Driver 0 direction has a pulldown resistor on v1.0  boards only
	// Driver 5 direction has a pulldown resistor on 1.01 boards only
	SetPinMode(DIRECTION_PINS[0], INPUT_PULLUP, false);
	SetPinMode(DIRECTION_PINS[1], INPUT_PULLUP, false);
	SetPinMode(DIRECTION_PINS[5], INPUT_PULLUP, false);
	delayMicroseconds(20); // give the pullup resistor time to work
	if (digitalRead(DIRECTION_PINS[5]))
	{
		return (digitalRead(DIRECTION_PINS[0])) ? BoardType::Duet3_6XD_v01 : BoardType::Duet3_6XD_v100;
	}
	return (digitalRead(DIRECTION_PINS[1])) ? BoardType::Duet3_6XD_v101 : BoardType::Duet3_6XD_v102;
}

#endif

// Set the board type/revision. This must be called quite early, because for some builds it relies on pins not having
// been programmed for their intended use yet. Also do any specific initialisation that varies with the board revision.
void Platform::SetBoardType() noexcept
{
#if defined(DUET3MINI_V04)
	// Test whether this is a WiFi or an Ethernet board by testing for a pulldown resistor on Dir1
	SetPinMode(DIRECTION_PINS[1], INPUT_PULLUP, false);
	SetPinMode(DIRECTION_PINS[2], INPUT_PULLUP, false);
	delayMicroseconds(20); // give the pullup resistor time to work
	board = (digitalRead(DIRECTION_PINS[1]))
				? ((digitalRead(DIRECTION_PINS[2])) ? BoardType::Duet3Mini_WiFi : BoardType::Duet3Mini_WiFi_ESP32)
				: BoardType::Duet3Mini_Ethernet;
#elif defined(DUET3_MB6HC)
	board = GetMB6HCBoardType();
	if (board >= BoardType::Duet3_6HC_v102)
	{
		powerMonitorVoltageRange = PowerMonitorVoltageRange_v102;
		DiagPin = DiagPin102;
		ActLedPin = ActLedPin102;
		DiagOnPolarity = DiagOnPolarity102;
	}
	else
	{
		powerMonitorVoltageRange = PowerMonitorVoltageRange_v101;
		DiagPin = DiagPinPre102;
		ActLedPin = ActLedPinPre102;
		DiagOnPolarity = DiagOnPolarityPre102;
	}
	driverPowerOnAdcReading = PowerVoltageToAdcReading(10.0);
	driverPowerOffAdcReading = PowerVoltageToAdcReading(9.5);
#elif defined(DUET3_MB6XD)
	board = GetMB6XDBoardType();
#elif defined(FMDC_V03)
	board = BoardType::FMDC;
#elif defined(DUET_NG)
	// Get ready to test whether the Ethernet module is present, so that we avoid additional delays
	SetPinMode(W5500ModuleSensePin, INPUT_PULLUP); // set our UART receive pin to be an input pin and enable the pullup

	// Set up the VSSA sense pin. Older Duet WiFis don't have it connected, so we enable the pulldown resistor to keep
	// it inactive.
	SetPinMode(VssaSensePin, INPUT_PULLUP, false);
	delayMicroseconds(10);
	const bool vssaHighVal = digitalRead(VssaSensePin);
	SetPinMode(VssaSensePin, INPUT_PULLDOWN);
	delayMicroseconds(10);
	const bool vssaLowVal = digitalRead(VssaSensePin);
	const bool vssaSenseWorking = vssaLowVal || !vssaHighVal;
	if (vssaSenseWorking)
	{
		SetPinMode(VssaSensePin, INPUT, true);
	}

#  if defined(USE_SBC)
	board = (vssaSenseWorking) ? BoardType::Duet2SBC_102 : BoardType::Duet2SBC_10;
#  else
	// Test whether the Ethernet module is present
	if (digitalRead(W5500ModuleSensePin)) // the Ethernet module has this pin grounded
	{
		board = (vssaSenseWorking) ? BoardType::DuetWiFi_102 : BoardType::DuetWiFi_10;
	}
	else
	{
		board = (vssaSenseWorking) ? BoardType::DuetEthernet_102 : BoardType::DuetEthernet_10;
	}
#  endif
#elif defined(DUET_M)
	board = BoardType::DuetM_10;
#elif defined(PCCB_10)
	board = BoardType::PCCB_v10;
#elif defined INDX
	board = BoardType::Indx;
#else
#  error Undefined board type
#endif
}

// Get a string describing the electronics
const char* _ecv_array Platform::GetElectronicsString() const noexcept
{
	switch (board)
	{
#if defined(DUET3MINI_V04)
	case BoardType::Duet3Mini_Unknown:
		return "Duet 3 " BOARD_SHORT_NAME " unknown variant";
	case BoardType::Duet3Mini_WiFi:
		return "Duet 3 " BOARD_SHORT_NAME " WiFi 1.02 or earlier";
	case BoardType::Duet3Mini_Ethernet:
		return "Duet 3 " BOARD_SHORT_NAME " Ethernet";
	case BoardType::Duet3Mini_WiFi_ESP32:
		return "Duet 3 " BOARD_SHORT_NAME " WiFi 1.03 or later";
#elif defined(DUET3_MB6HC)
	case BoardType::Duet3_6HC_v06_100:
		return "Duet 3 " BOARD_SHORT_NAME " v1.0 or earlier";
	case BoardType::Duet3_6HC_v101:
		return "Duet 3 " BOARD_SHORT_NAME " v1.01";
	case BoardType::Duet3_6HC_v102:
		return "Duet 3 " BOARD_SHORT_NAME " v1.02 or 1.02a";
	case BoardType::Duet3_6HC_v102b:
		return "Duet 3 " BOARD_SHORT_NAME " v1.02b";
	case BoardType::Duet3_6HC_v102c:
		return "Duet 3 " BOARD_SHORT_NAME " v1.02c or later";
#elif defined(DUET3_MB6XD)
	case BoardType::Duet3_6XD_v01:
		return "Duet 3 " BOARD_SHORT_NAME " v0.1";
	case BoardType::Duet3_6XD_v100:
		return "Duet 3 " BOARD_SHORT_NAME " v1.0";
	case BoardType::Duet3_6XD_v101:
		return "Duet 3 " BOARD_SHORT_NAME " v1.01";
	case BoardType::Duet3_6XD_v102:
		return "Duet 3 " BOARD_SHORT_NAME " v1.02 or later";
#elif defined(FMDC_V03)
	case BoardType::FMDC:
		return "Duet 3 " BOARD_SHORT_NAME;
#elif defined(DUET_NG)
	// This is the string that the Duet 2 ATE uses to identify the board. The version number must be at the end.
	case BoardType::DuetWiFi_10:
		return "Duet WiFi 1.0 or 1.01";
	case BoardType::DuetWiFi_102:
		return "Duet WiFi 1.02 or later";
	case BoardType::DuetEthernet_10:
		return "Duet Ethernet 1.0 or 1.01";
	case BoardType::DuetEthernet_102:
		return "Duet Ethernet 1.02 or later";
	case BoardType::Duet2SBC_10:
		return "Duet 2 + SBC 1.0 or 1.01";
	case BoardType::Duet2SBC_102:
		return "Duet 2 + SBC 1.02 or later";
#elif defined(DUET_M)
	case BoardType::DuetM_10:
		return "Duet Maestro 1.0";
#elif defined(PCCB_10)
	case BoardType::PCCB_v10:
		return "PC001373";
#elif defined(INDX)
	case BoardType::Indx:
		return "INDX";
#else
#  error Undefined board type
#endif
	default:
		return "Unidentified";
	}
}

// Get the board string
const char* _ecv_array Platform::GetBoardString() const noexcept
{
	switch (board)
	{
#if defined(DUET3MINI_V04)
	case BoardType::Duet3Mini_Unknown:
		return "duet5lcunknown";
	case BoardType::Duet3Mini_WiFi:
		return "duet5lcwifi";
	case BoardType::Duet3Mini_WiFi_ESP32:
		return "duet5lcwifi32";
	case BoardType::Duet3Mini_Ethernet:
		return "duet5lcethernet";
#elif defined(DUET3_MB6HC)
	case BoardType::Duet3_6HC_v06_100:
		return "duet3mb6hc100";
	case BoardType::Duet3_6HC_v101:
		return "duet3mb6hc101";
	case BoardType::Duet3_6HC_v102:
		return "duet3mb6hc102";
	case BoardType::Duet3_6HC_v102b:
		return "duet3mb6hc102b";
#elif defined(DUET3_MB6XD)
	case BoardType::Duet3_6XD_v01:
		return "duet3mb6xd001";
	case BoardType::Duet3_6XD_v100:
		return "duet3mb6xd100";
	case BoardType::Duet3_6XD_v101:
		return "duet3mb6xd101";
	case BoardType::Duet3_6XD_v102:
		return "duet3mb6xd102";
#elif defined(FMDC_V03)
	case BoardType::FMDC:
		return "fmdc";
#elif defined(DUET_NG)
	case BoardType::DuetWiFi_10:
		return "duetwifi10";
	case BoardType::DuetWiFi_102:
		return "duetwifi102";
	case BoardType::DuetEthernet_10:
		return "duetethernet10";
	case BoardType::DuetEthernet_102:
		return "duetethernet102";
	case BoardType::Duet2SBC_10:
		return "duet2sbc10";
	case BoardType::Duet2SBC_102:
		return "duet2sbc102";
#elif defined(DUET_M)
	case BoardType::DuetM_10:
		return "duetmaestro100";
#elif defined(PCCB_10)
	case BoardType::PCCB_v10:
		return "pc001373";
#elif defined(INDX)
	case BoardType::Indx:
		return "indx";
#else
#  error Undefined board type
#endif
	default:
		return "unknown";
	}
}

#ifdef DUET_NG

// Return true if this is a Duet WiFi, false if it is a Duet Ethernet
bool Platform::IsDuetWiFi() const noexcept
{
	return board == BoardType::DuetWiFi_10 || board == BoardType::DuetWiFi_102;
}

const char* _ecv_array Platform::GetBoardName() const noexcept
{
	return (board == BoardType::Duet2SBC_10 || board == BoardType::Duet2SBC_102) ? BOARD_NAME_SBC
		   : (IsDuetWiFi())														 ? BOARD_NAME_WIFI
																				 : BOARD_NAME_ETHERNET;
}

const char* _ecv_array Platform::GetBoardShortName() const noexcept
{
	return (board == BoardType::Duet2SBC_10 || board == BoardType::Duet2SBC_102) ? BOARD_SHORT_NAME_SBC
		   : (IsDuetWiFi())														 ? BOARD_SHORT_NAME_WIFI
																				 : BOARD_SHORT_NAME_ETHERNET;
}

#endif

#ifdef DUET3MINI_V04

// Return true if this is a WiFi board, false if it has Ethernet
bool Platform::IsDuetWiFi() const noexcept
{
	return board == BoardType::Duet3Mini_WiFi || board == BoardType::Duet3Mini_WiFi_ESP32 ||
		   board == BoardType::Duet3Mini_Unknown;
}

bool Platform::HasESP32() const noexcept
{
	return board == BoardType::Duet3Mini_WiFi_ESP32;
}

#endif

#if HAS_WIFI_NETWORKING

const char* _ecv_array Platform::GetDefaultWiFiFirmwareName() noexcept
{
#  ifdef DUET3MINI_V04
	return (HasESP32()) ? WIFI_FIRMWARE_FILE_ESP32 : WIFI_FIRMWARE_FILE_ESP8266;
#  else
	return WIFI_FIRMWARE_FILE;
#  endif
}

#endif

#if HAS_MASS_STORAGE || HAS_SBC_INTERFACE || HAS_EMBEDDED_FILES

ReadLockedPointer<const char> ConfigurableFolder::GetLockedPointer() const noexcept
{
	return {lock, GetUnlockedPointer()};
}

#endif

#if HAS_MASS_STORAGE || HAS_EMBEDDED_FILES

void ConfigurableFolder::AppendToString(const StringRef& path) const noexcept
{
	const ReadLocker locker(lock);
	path.cat(GetUnlockedPointer());
}

#endif

#if HAS_CPU_TEMP_SENSOR

// CPU temperature
MinCurMax Platform::GetMcuTemperatures() const noexcept
{
	MinCurMax result{};
	result.minimum = lowestMcuTemperature;
	result.current = GetCpuTemperature();
	result.maximum = highestMcuTemperature;
	return result;
}

#endif

#if HAS_VOLTAGE_MONITOR

// Power in voltage
MinCurMax Platform::GetPowerVoltages() const noexcept
{
	MinCurMax result{};
	result.minimum = AdcReadingToPowerVoltage(lowestVin);
	result.current = AdcReadingToPowerVoltage(currentVin);
	result.maximum = AdcReadingToPowerVoltage(highestVin);
	return result;
}

float Platform::GetCurrentPowerVoltage() const noexcept
{
	return AdcReadingToPowerVoltage(currentVin);
}

#endif

#if HAS_12V_MONITOR

MinCurMax Platform::GetV12Voltages() const noexcept
{
	MinCurMax result{};
	result.minimum = AdcReadingToV12Voltage(lowestV12);
	result.current = AdcReadingToV12Voltage(currentV12);
	result.maximum = AdcReadingToV12Voltage(highestV12);
	return result;
}

float Platform::GetCurrentV12Voltage() const noexcept
{
	return AdcReadingToV12Voltage(currentV12);
}

#endif

// Real-time clock

bool Platform::SetDateTime(time_t tim) noexcept
{
	struct tm brokenDateTime
	{
	};
	const bool ok = (gmtime_r(&tim, &brokenDateTime) != nullptr);
	if (ok)
	{
		realTime = tim; // set the date and time

		// Write a log message, giving the time since power up in same format as the logger does
		const auto timeSincePowerUp = (uint32_t)(millis64() / 1000u);
		MessageF(LogWarn,
				 "Date and time set at power up + %02" PRIu32 ":%02" PRIu32 ":%02" PRIu32 "\n",
				 timeSincePowerUp / 3600u,
				 (timeSincePowerUp % 3600u) / 60u,
				 timeSincePowerUp % 60u);
		timeLastUpdatedMillis = millis();
	}
	return ok;
}

#if SUPPORT_CAN_EXPANSION

// Call this when we have processed a message, other than regular broadcast messages. It causes the ACT LED to flash.
void Platform::OnProcessingCanMessage() noexcept
{
	whenLastCanMessageProcessed = millis();
	digitalWrite(ActLedPin, ActOnPolarity); // turn the ACT LED on
}

#endif

#if MCU_HAS_UNIQUE_ID

// Get a pseudo-random number (not a true random number)
uint32_t Platform::Random() noexcept
{
	return StepTimer::GetTimerTicks() ^ uniqueId.GetHash();
}

#endif

void Platform::SetDiagLed(bool on) const noexcept
{
	digitalWrite(DiagPin, XNor(DiagOnPolarity, on));
}

#if SUPPORT_MULTICAST_DISCOVERY

void Platform::InvertDiagLed() const noexcept
{
	digitalWrite(DiagPin, !digitalRead(DiagPin));
}

#endif

#if HAS_CPU_TEMP_SENSOR && SAME5x

void Platform::TemperatureCalibrationInit() noexcept
{
	// Temperature sense stuff
	constexpr uint32_t NVM_TEMP_CAL_TLI_POS = 0;
	constexpr uint32_t NVM_TEMP_CAL_TLI_SIZE = 8;
	constexpr uint32_t NVM_TEMP_CAL_TLD_POS = 8;
	constexpr uint32_t NVM_TEMP_CAL_TLD_SIZE = 4;
	constexpr uint32_t NVM_TEMP_CAL_THI_POS = 12;
	constexpr uint32_t NVM_TEMP_CAL_THI_SIZE = 8;
	constexpr uint32_t NVM_TEMP_CAL_THD_POS = 20;
	constexpr uint32_t NVM_TEMP_CAL_THD_SIZE = 4;
	constexpr uint32_t NVM_TEMP_CAL_VPL_POS = 40;
	constexpr uint32_t NVM_TEMP_CAL_VPL_SIZE = 12;
	constexpr uint32_t NVM_TEMP_CAL_VPH_POS = 52;
	constexpr uint32_t NVM_TEMP_CAL_VPH_SIZE = 12;
	constexpr uint32_t NVM_TEMP_CAL_VCL_POS = 64;
	constexpr uint32_t NVM_TEMP_CAL_VCL_SIZE = 12;
	constexpr uint32_t NVM_TEMP_CAL_VCH_POS = 76;
	constexpr uint32_t NVM_TEMP_CAL_VCH_SIZE = 12;

	const uint16_t temp_cal_vpl =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_VPL_POS / 32)) >> (NVM_TEMP_CAL_VPL_POS % 32)) &
		((1u << NVM_TEMP_CAL_VPL_SIZE) - 1);
	const uint16_t temp_cal_vph =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_VPH_POS / 32)) >> (NVM_TEMP_CAL_VPH_POS % 32)) &
		((1u << NVM_TEMP_CAL_VPH_SIZE) - 1);
	const uint16_t temp_cal_vcl =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_VCL_POS / 32)) >> (NVM_TEMP_CAL_VCL_POS % 32)) &
		((1u << NVM_TEMP_CAL_VCL_SIZE) - 1);
	const uint16_t temp_cal_vch =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_VCH_POS / 32)) >> (NVM_TEMP_CAL_VCH_POS % 32)) &
		((1u << NVM_TEMP_CAL_VCH_SIZE) - 1);

	const uint8_t temp_cal_tli =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_TLI_POS / 32)) >> (NVM_TEMP_CAL_TLI_POS % 32)) &
		((1u << NVM_TEMP_CAL_TLI_SIZE) - 1);
	const uint8_t temp_cal_tld =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_TLD_POS / 32)) >> (NVM_TEMP_CAL_TLD_POS % 32)) &
		((1u << NVM_TEMP_CAL_TLD_SIZE) - 1);
	const uint16_t temp_cal_tl = ((uint16_t)temp_cal_tli) << 4 | ((uint16_t)temp_cal_tld);

	const uint8_t temp_cal_thi =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_THI_POS / 32)) >> (NVM_TEMP_CAL_THI_POS % 32)) &
		((1u << NVM_TEMP_CAL_THI_SIZE) - 1);
	const uint8_t temp_cal_thd =
		(*((uint32_t*)(NVMCTRL_TEMP_LOG) + (NVM_TEMP_CAL_THD_POS / 32)) >> (NVM_TEMP_CAL_THD_POS % 32)) &
		((1u << NVM_TEMP_CAL_THD_SIZE) - 1);
	const uint16_t temp_cal_th = ((uint16_t)temp_cal_thi) << 4 | ((uint16_t)temp_cal_thd);

	tempCalF1 = (int32_t)temp_cal_tl * (int32_t)temp_cal_vph - (int32_t)temp_cal_th * (int32_t)temp_cal_vpl;
	tempCalF2 = (int32_t)temp_cal_tl * (int32_t)temp_cal_vch - (int32_t)temp_cal_th * (int32_t)temp_cal_vcl;
	tempCalF3 = (int32_t)temp_cal_vcl - (int32_t)temp_cal_vch;
	tempCalF4 = (int32_t)temp_cal_vpl - (int32_t)temp_cal_vph;
}

#endif

void Platform::Tick() noexcept
{
#if !SAME5x
	LegacyAnalogIn::AnalogInFinaliseConversion();
#endif

#if HAS_VOLTAGE_MONITOR || HAS_12V_MONITOR
	if (tickState != 0)
	{
#  if HAS_VOLTAGE_MONITOR
		// Read the power input voltage
		currentVin = AnalogInReadChannel(vInMonitorAdcChannel);
		if (currentVin > highestVin)
		{
			highestVin = currentVin;
		}
		if (currentVin < lowestVin ||
			millis64() < 1000) // don't record the lowest VIN voltage while we are still powering up
		{
			lowestVin = currentVin;
		}
#  endif

#  if HAS_12V_MONITOR
		currentV12 = AnalogInReadChannel(v12MonitorAdcChannel);
		if (currentV12 > highestV12)
		{
			highestV12 = currentV12;
		}
		if (currentV12 < lowestV12 ||
			millis64() < 1000) // don't record the lowest V12 voltage while we are still powering up
		{
			lowestV12 = currentV12;
		}
#  endif
	}
#endif

#if SAME70
	// The SAME70 ADC is noisy, so read a thermistor on every tick so that we can average a greater number of readings
	// Because we are in the tick ISR and no other ISR reads the averaging filter, we can cast away 'volatile' here.
	if (tickState != 0)
	{
		auto& currentFilter =
			// that is written from the ADC callback and read here
			// NOLINTNEXTLINE(cppcoreguidelines-pro-type-const-cast) - drops volatile on an ADC filter
			const_cast<ThermistorAveragingFilter&>(adcFilters[currentFilterNumber]); // cast away 'volatile'
		currentFilter.ProcessReading(AnalogInReadChannel(filteredAdcChannels[currentFilterNumber]));

		++currentFilterNumber;
		if (currentFilterNumber == NumAdcFilters)
		{
			currentFilterNumber = 0;
		}
	}
#endif

	tickState = 1;

#if SAME70
	// On Duet 3, AFEC1 is used only for thermistors and associated Vref/Vssa monitoring. AFEC0 is used for everything
	// else. To reduce noise, we use x16 hardware averaging on AFEC0 and x256 on AFEC1. This is hard coded in file
	// AnalogIn.cpp in project CoreNG. There is enough time to convert all AFEC0 channels in one tick, but only one
	// AFEC1 channel because of the higher averaging.
	LegacyAnalogIn::AnalogInStartConversion(0x0FFF | (1u << (uint8_t)filteredAdcChannels[currentFilterNumber]));
#elif !SAME5x
	LegacyAnalogIn::AnalogInStartConversion();
#endif
}

// Pragma pop_options is not supported on this platform
// #pragma GCC pop_options

// End
