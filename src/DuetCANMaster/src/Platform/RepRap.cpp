#include "RepRap.h"
#include <General/IapInfo.h>

#include <Devices.h>
#include <Movement/StepTimer.h>
#include "Platform.h"
#include "Tasks.h"
#include <Cache.h>
#include <Hardware/SoftwareReset.h>
#include <Hardware/ExceptionHandlers.h>
#include <CoreNotifyIndices.h>
#include "Version.h"

#if HAS_SBC_INTERFACE
# include <SBC/SbcInterface.h>
#endif

#if SUPPORT_CAN_EXPANSION
# include <CAN/CanInterface.h>
# include <CAN/ExpansionManager.h>
#endif

#include "FreeRTOS.h"
#include "task.h"

#if SAME70
# include <DmacManager.h>
#endif

#if SAM4S
# include <wdt/wdt.h>
#endif

// We call vTaskNotifyGiveFromISR from various interrupts, so the following must be true
static_assert(configLIBRARY_MAX_SYSCALL_INTERRUPT_PRIORITY <= NvicPriorityHSMCI, "configMAX_SYSCALL_INTERRUPT_PRIORITY is set too high");

// This is the string that identifies the board type and firmware version, that the vector at 0x20 points to.
// The characters after the last space must be the firmware version in standard format, e.g. "3.3.0" or "3.4.0beta4". The firmware build date/time is not included.
extern const char VersionText[] = FIRMWARE_NAME " version " VERSION;

#if HAS_HIGH_SPEED_SD && !SAME5x										// SAME5x uses CoreN2G which makes its own RTOS calls

static TaskHandle _ecv_null hsmciTask = nullptr;						// the task that is waiting for a HSMCI command to complete

// HSMCI interrupt handler
extern "C" void HSMCI_Handler() noexcept
{
	HSMCI->HSMCI_IDR = 0xFFFFFFFFu;										// disable all HSMCI interrupts
#if SAME70
	XDMAC->XDMAC_CHID[DmacChanHsmci].XDMAC_CID = 0xFFFFFFFFu;			// disable all DMA interrupts for this channel
#endif
	TaskBase::GiveFromISR(hsmciTask, NotifyIndices::Sdhc);				// wake up the task
}

#if SAME70

// HSMCI DMA complete callback
void HsmciDmaCallback(CallbackParameter cb, DmaCallbackReason reason) noexcept
{
	HSMCI->HSMCI_IDR = 0xFFFFFFFFu;										// disable all HSMCI interrupts
	XDMAC->XDMAC_CHID[DmacChanHsmci].XDMAC_CID = 0xFFFFFFFFu;			// disable all DMA interrupts for this channel
	if (hsmciTask != nullptr)
	{
		TaskBase::GiveFromISR(hsmciTask, NotifyIndices::Sdhc);
		hsmciTask = nullptr;
	}
}

#endif

// Callback function from the hsmci driver, called while it is waiting for an SD card operation to complete
// 'stBits' is the set of bits in the HSMCI status register that the caller is interested in.
// The caller keeps calling this function until at least one of those bits is set.
extern "C" void hsmciIdle(uint32_t stBits, uint32_t dmaBits) noexcept
{
	if (   (HSMCI->HSMCI_SR & stBits) == 0
#if SAME70
		&& (XDMAC->XDMAC_CHID[DmacChanHsmci].XDMAC_CIS & dmaBits) == 0
#endif
	   )
	{
		// Suspend this task until we get an interrupt indicating that a status bit that we are interested in has been set
		hsmciTask = TaskBase::GetCallerTaskHandle();
		HSMCI->HSMCI_IER = stBits;
#if SAME70
		DmacManager::SetInterruptCallback(DmacChanHsmci, HsmciDmaCallback, CallbackParameter());
		XDMAC->XDMAC_CHID[DmacChanHsmci].XDMAC_CIE = dmaBits;
		XDMAC->XDMAC_GIE = 1u << DmacChanHsmci;
#endif
		if (!TaskBase::TakeIndexed(NotifyIndices::Sdhc, 200))
		{
			// We timed out waiting for the HSMCI operation to complete
			reprap.GetPlatform().LogError(ErrorCode::HsmciTimeout);
		}
	}
}

#endif //end if HAS_HIGH_SPEED_SD && !SAME5x

constexpr unsigned int StateSubTableNumber = 3;		// section number of 'state' in the following

// RepRap member functions.

// Do nothing more in the constructor; put what you want in RepRap:Init()

RepRap::RepRap() noexcept
	: boardsSeq(0), directoriesSeq(0), fansSeq(0), heatSeq(0), inputsSeq(0), jobSeq(0), ledStripsSeq(0), moveSeq(0), globalSeq(0),
	  networkSeq(0), sensorsSeq(0), spindlesSeq(0), stateSeq(0), toolsSeq(0), volumesSeq(0),
	  lastWarningMillis(0),
	  ticksInSpinState(0), heatTaskIdleTicks(0),
	  beepFrequency(0), beepDuration(0), beepTimer(0),
	  spinningModule(Module::numModules), stopped(false), active(false), processingConfig(true)
{
	// Don't call constructors for other objects here
}

void RepRap::Init() noexcept
{
	OutputBuffer::Init();

	platform = new Platform();
#if HAS_SBC_INTERFACE
	sbcInterface = new SbcInterface();				// needs to be allocated early on Duet 2 so as to avoid using any of the last 64K of RAM
#endif

#if SUPPORT_CAN_EXPANSION
	expansion = new ExpansionManager();
#endif

	platform->Init();
#if SUPPORT_CAN_EXPANSION
	CanInterface::Init();
#endif

	// sbcInterface is not initialised until we know we are using it, to prevent a disconnected SBC interface generating interrupts and DMA

	// Set up the timeout of the regular watchdog, and set up the backup watchdog if there is one.
#if SAME5x
	WatchdogInit();
	NVIC_SetPriority(WDT_IRQn, NvicPriorityWatchdog);								// set priority for watchdog interrupts
	NVIC_ClearPendingIRQ(WDT_IRQn);
	NVIC_EnableIRQ(WDT_IRQn);														// enable the watchdog early warning interrupt
#else
	{
		// The clock frequency for both watchdogs is about 32768/128 = 256Hz
		// The watchdogs on the SAM4E seem to be very timing-sensitive. On the Duet WiFi/Ethernet they were going off spuriously depending on how long the DueX initialisation took.
		// The documentation says you mustn't write to the mode register within 3 slow clocks after kicking the watchdog.
		// I have a theory that the converse is also true, i.e. after enabling the watchdog you mustn't kick it within 3 slow clocks
		// So I've added a delay call before we set 'active' true (which enables kicking the watchdog), and that seems to fix the problem.
# if SAM4E || SAME70
		const uint16_t mainTimeout = 49152/128;										// set main (back stop) watchdog timeout to 1.5s second (max allowed value is 4095 = 16 seconds)
		WDT->WDT_MR = WDT_MR_WDRSTEN | WDT_MR_WDDBGHLT | WDT_MR_WDV(mainTimeout) | WDT_MR_WDD(mainTimeout);	// reset the processor on a watchdog fault, stop it when debugging

		// The RSWDT must be initialised *after* the main WDT
		const uint16_t rsTimeout = 32768/128;										// set secondary watchdog timeout to 1 second (max allowed value is 4095 = 16 seconds)
#  if SAME70
		RSWDT->RSWDT_MR = RSWDT_MR_WDFIEN | RSWDT_MR_WDDBGHLT | RSWDT_MR_WDV(rsTimeout) | RSWDT_MR_ALLONES_Msk;		// generate an interrupt on a watchdog fault
		NVIC_SetPriority(RSWDT_IRQn, NvicPriorityWatchdog);							// set priority for watchdog interrupts
		NVIC_ClearPendingIRQ(RSWDT_IRQn);
		NVIC_EnableIRQ(RSWDT_IRQn);													// enable the watchdog interrupt
#  else
		RSWDT->RSWDT_MR = RSWDT_MR_WDFIEN | RSWDT_MR_WDDBGHLT | RSWDT_MR_WDV(rsTimeout) | RSWDT_MR_WDD(rsTimeout);	// generate an interrupt on a watchdog fault
		NVIC_SetPriority(WDT_IRQn, NvicPriorityWatchdog);							// set priority for watchdog interrupts
		NVIC_ClearPendingIRQ(WDT_IRQn);
		NVIC_EnableIRQ(WDT_IRQn);													// enable the watchdog interrupt
#  endif
# else
		// We don't have a RSWDT so set the main watchdog timeout to 1 second
		const uint16_t timeout = 32768/128;											// set watchdog timeout to 1 second (max allowed value is 4095 = 16 seconds)
		wdt_init(WDT, WDT_MR_WDRSTEN | WDT_MR_WDDBGHLT, timeout, timeout);			// reset the processor on a watchdog fault, stop it when debugging
# endif
		delayMicroseconds(200);														// 200us is about 6 slow clocks
	}
#endif

	active = true;										// must do this after we initialise the watchdog but before we start the network or call Spin(), else the watchdog may time out

	delay(100);											// give the tick ISR time to collect voltage readings
	platform->ResetVoltageMonitors();					// get rid of the spurious zero minimum voltage readings

#if 0	//DEBUG
# if !SAME70
	SCnSCB->ACTLR |= SCnSCB_ACTLR_DISDEFWBUF_Msk;		// disable write buffer
# endif
	delay(5000);										// give me time to connect YAT before much else happens
#endif

	platform->MessageF(UsbMessage, "%s\n", VersionText);

#if HAS_SBC_INTERFACE
# if defined(DUET_NG)
	// It's the SBC build of Duet 2 firmware. Enable the PanelDue port so that the ATE can test it.
	platform->EnablePanelDuePort();
# endif

	sbcInterface->Init();

	// Keep spinning until the SBC connects
	while (!sbcInterface->IsConnected())
	{
		Spin();
	}
#endif
	processingConfig = false;

	platform->MessageF(UsbMessage, "%s is up and running.\n", FIRMWARE_NAME);

	fastLoop = UINT32_MAX;
	slowLoop = 0;
}

void RepRap::Exit() noexcept
{
	active = false;
	platform->Exit();
}

void RepRap::Spin() noexcept
{
	if (!active)
	{
		return;
	}

	ticksInSpinState = 0;
	spinningModule = Module::Platform;
	platform->Spin();

#if SUPPORT_CAN_EXPANSION
	ticksInSpinState = 0;
	spinningModule = Module::Expansion;
	expansion->Spin();
#endif

	ticksInSpinState = 0;
	spinningModule = Module::numModules;

	RTOSIface::Yield();
}

// Send diagnostics to the specified destination. This is in a separate function so that the large string doesn't take up main task stack space all the time.
__attribute__((noinline)) void RepRap::GenerateDeferredDiagnostics(MessageType destination) noexcept
{
	String<GCodeReplyLength> buf;
	Diagnostics(destination, buf.GetRef());
}

// Turn off the heaters, disable the motors, and deactivate the Heat, Move and GCodes classes. Leave everything else working.
void RepRap::EmergencyStop() noexcept
{
#ifdef DUET3_ATE
	Duet3Ate::PowerOffEUT();
#endif

	stopped = true;									// a useful side effect of setting this is that it prevents Platform::Tick being called, which is needed when loading IAP into RAM

	// Do not turn off ATX power here. If the nozzles are still hot, don't risk melting any surrounding parts by turning fans off.
	//platform->SetAtxPower(false);

#if SUPPORT_CAN_EXPANSION

	{
		expansion->EmergencyStop();
	}
#endif
}

void RepRap::ClearDebug() noexcept
{
	for (DebugFlags& dm : debugMaps)
	{
		dm.Clear();
	}
}

void RepRap::Tick() noexcept
{
	// Kicking the watchdog before it has been initialised may trigger it!
	if (active)
	{
		WatchdogReset();														// kick the watchdog

#if SAM4E || SAME70
		WatchdogResetSecondary();												// kick the secondary watchdog
#endif

		if (!stopped)
		{
			platform->Tick();
			++ticksInSpinState;
			if (ticksInSpinState >= MaxMainTaskTicksInSpinState)		// if we stall for 20 seconds, save diagnostic data and reset
			{
				stopped = true;

				// Save the stack of the stuck task when we get stuck in a spin loop
				const uint32_t *_ecv_array relevantStackPtr;

				// When a task gets stuck, sometimes we want the stack of that task and sometimes we want the stack of the running task instead
#if 1
				// Record the stack of the running task
				const TaskHandle relevantTask = RTOSIface::GetCurrentTask();
				if (relevantTask != nullptr)
#else
				// Record the stack of the stuck task
				const TaskHandle relevantTask = (heatTaskStuck) ? Heat::GetHeatTask() : Tasks::GetMainTask();
				if (relevantTask == RTOSIface::GetCurrentTask())
#endif
				{
#ifdef __ECV__
					// eCv doesn't understand the gcc "register const... asm" line
					const uint32_t *_ecv_array stackPtr = _ecv_undefined(const uint32_t *_ecv_array);
#else
					__asm volatile("mrs r2, psp");
					register const uint32_t *_ecv_array stackPtr asm ("r2");	// we want the PSP not the MSP
#endif
					relevantStackPtr = stackPtr + 5;							// discard uninteresting registers, keep LR PC PSR
				}
				else
				{
					relevantStackPtr = const_cast<const uint32_t *_ecv_array>(pxTaskGetLastStackTop(relevantTask->GetFreeRTOSHandle()));
					// All registers were saved on the stack, so to get useful return addresses we need to skip most of them.
					// See the port.c files in FreeRTOS for the stack layouts
#if SAME70 || SAM4E || SAME5x
					// ARM Cortex M7 with double precision floating point, or ARM Cortex M4F
					if ((relevantStackPtr[8] & 0x10) == 0)						// test EXC_RETURN FP bit
					{
						relevantStackPtr += 9 + 16;								// skip r4-r11 and r14 and s16-s31
					}
					else
					{
						relevantStackPtr += 9;									// skip r4-r11 and r14
					}
#else
					// ARM Cortex M3 or M4 without floating point
					relevantStackPtr += 8;										// skip r4-r11
#endif
				}
				SoftwareReset(SoftwareResetReason::stuckInSpin, relevantStackPtr);
			}
		}
	}
}

// Return true if we are close to timeout
bool RepRap::SpinTimeoutImminent() const noexcept
{
	return ticksInSpinState >= HighMainTaskTicksInSpinState;
}

// Helper function for diagnostic tests in Platform.cpp, to cause a deliberate divide-by-zero
/*static*/ uint32_t RepRap::DoDivide(uint32_t a, uint32_t b) noexcept
{
	return a/b;
}

// Helper function for diagnostic tests in Platform.cpp, to cause a deliberate OOM fault
/*static*/ void RepRap::DoMemoryLeak() noexcept
{
	void * leak;
	while (true)
	{
		leak = Tasks::AllocPermanent(1024); 	// Allocate memory continuously
		(void)leak;								// Prevent unused variable warning
	}
}

// Helper function for diagnostic tests in Platform.cpp, to cause a deliberate bus fault or memory protection error
/*static*/ void RepRap::GenerateBusFault() noexcept
{
#if SAME5x
	(void)*(reinterpret_cast<const volatile char*>(0x30000000));
#elif SAME70
	(void)*(reinterpret_cast<const volatile char*>(0x30000000));
#elif SAM4E || SAM4S
	(void)*(reinterpret_cast<const volatile char*>(0x20800000));
#elif SAM3XA
	(void)*(reinterpret_cast<const volatile char*>(0x20200000));
#else
# error Unsupported processor
#endif
}

// Helper function for diagnostic tests in Platform.cpp, to calculate sine and cosine
/*static*/ float RepRap::SinfCosf(float angle) noexcept
{
	return sinf(angle) + cosf(angle);
}

// Report an internal error
void RepRap::ReportInternalError(c_string file, c_string func, int line) const noexcept
{
	platform->MessageF(ErrorMessage, "Internal Error in %s at %s(%d)\n", func, file, line);
}

// End
