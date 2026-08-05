/*
 * DataTransfer.cpp
 *
 *  Created on: 29 Mar 2019
 *      Author: Christian
 */

#include "DataTransfer.h"

#if HAS_SBC_INTERFACE

#  include "SbcInterface.h"
#  include <AppNotifyIndices.h>

#  include <Movement/StepTimer.h>
#  include <Platform/OutputMemory.h>
#  include <Storage/CRC32.h>
#  include <algorithm>

#  if SUPPORTS_SBC_OVER_USB
#	include <Devices.h>
#  endif

#  if defined(DUET_NG) && defined(USE_SBC)

// The PDC seems to be too slow to work reliably without getting transmit underruns, so we use the DMAC now.
#	define USE_DMAC 1		   // use general DMA controller
#	define USE_XDMAC 0		   // use XDMA controller
#	define USE_DMAC_MANAGER 0 // use SAME5x DmacManager module

#  elif defined(DUET3)

#	define USE_DMAC 0		   // use general DMA controller
#	define USE_XDMAC 1		   // use XDMA controller
#	define USE_DMAC_MANAGER 0 // use SAME5x DmacManager module

#  elif defined(DUET3MINI)

#	define USE_DMAC 0		   // use general DMA controller
#	define USE_XDMAC 0		   // use XDMA controller
#	define USE_DMAC_MANAGER 1 // use SAME5x DmacManager module
constexpr IRQn SBC_SPI_IRQn = SbcSpiSercomIRQn;

#  else
#	error Unknown board
#  endif

#  if USE_DMAC
#	include <dmac/dmac.h>
#	include <matrix/matrix.h>
#	include <pmc/pmc.h>
#	include <spi/spi.h>
#  endif

#  if USE_XDMAC
#	include <xdmac/xdmac.h>
#  endif

#  if USE_DMAC_MANAGER || SAME70
#	include <DmacManager.h>
#  endif

#  if SAME70
#	include <spi/spi.h>
#  endif

#  if SAME5x
#	include <Serial.h>
#  endif

#  if defined(DUET3_MB6HC) && HAS_WIFI_NETWORKING
extern void ESP_SPI_HANDLER() noexcept;
#  endif

#  include <Cache.h>
#  include <Platform/OutputMemory.h>
#  include <Platform/RepRap.h>
#  include <RTOSIface/RTOSIface.h>
#  include <RepRapFirmware.h>

#  include <General/IP4String.h>

static TaskHandle sbcTaskHandle = nullptr;

#  if USE_DMAC

// Hardware IDs of the SPI transmit and receive DMA interfaces. See atsam datasheet.
const uint32_t SBC_SPI_TX_DMA_HW_ID = 1;
const uint32_t SBC_SPI_RX_DMA_HW_ID = 2;

#  endif

#  if USE_XDMAC

// XDMAC hardware, see datasheet
constexpr uint32_t sbcSpiTxPerid = (uint32_t)DmaTrigSource::spi1tx;
constexpr uint32_t sbcSpiRxPerid = (uint32_t)DmaTrigSource::spi1rx;

static xdmac_channel_config_t xdmacTxCfg, xdmacRxCfg;

#  endif

volatile bool dataReceived =
	false; // warning: on the SAME5x this just means the transfer has started, not necessarily that it has ended!
std::atomic<unsigned int> spiTxUnderruns = 0, spiRxOverruns = 0;
#  if !SAME5x
static volatile bool spiTransferStarted = false;
#  endif

// Length in bytes we armed the last sub-exchange for, and how many of them the SBC did not clock. The
// SBC drives the clock, so it alone decides how long each sub-exchange is; comparing what it clocked
// against what we armed for is the only direct evidence we have that both sides agree on which
// sub-exchange this was. The residual must be sampled before the DMA channels are torn down, because
// disabling them discards the count
static volatile size_t spiArmedLength = 0;
static volatile uint32_t spiRxResidual = 0;

static volatile void* spiRxBuffer = nullptr;
static const volatile void* spiTxBuffer = nullptr;

static uint32_t SpiDmaGetRxResidual() noexcept
{
#  if USE_XDMAC
	// CUBC counts down the microblock length, which for this channel is configured in bytes
	return XDMAC->XDMAC_CHID[DmacChanSbcRx].XDMAC_CUBC & XDMAC_CUBC_UBLEN_Msk;
#  else
	// Only implemented for the XDMAC (SAME70), the only controller DuetCANMaster currently runs on.
	// Reporting nothing outstanding leaves the short-transfer check inert rather than guessing at
	// another DMA controller's registers
	return 0;
#  endif
}

static void SpiDmaDisable() noexcept
{
#  if USE_DMAC
	dmac_channel_disable(DMAC, DmacChanSbcRx);
	dmac_channel_disable(DMAC, DmacChanSbcTx);
#  endif

#  if USE_XDMAC
	xdmac_channel_disable(XDMAC, DmacChanSbcRx);
	xdmac_channel_disable(XDMAC, DmacChanSbcTx);
#  endif

#  if USE_DMAC_MANAGER
	DmacManager::DisableChannel(DmacChanSbcRx);
	DmacManager::DisableChannel(DmacChanSbcTx);
#  endif
}

#  if !SAME5x
static bool SpiDmaCheckRxComplete() noexcept
{
#	if USE_DMAC
	const uint32_t status = DMAC->DMAC_CHSR;
	if (((status & (DMAC_CHSR_ENA0 << DmacChanSbcRx)) ==
		 0) // controller is not enabled, perhaps because it finished a full buffer transfer
		|| ((status & (DMAC_CHSR_EMPT0 << DmacChanSbcRx)) !=
			0) // controller is enabled, probably suspended, and the FIFO is empty
	)
	{
		// Disable the channel.
		// We also need to set the resume bit, otherwise it remains suspended when we re-enable it.
		DMAC->DMAC_CHDR = (DMAC_CHDR_DIS0 << DmacChanSbcRx) | (DMAC_CHDR_RES0 << DmacChanSbcRx);
		return true;
	}
	return false;

#	elif USE_XDMAC
	return (xdmac_channel_get_status(XDMAC) & ((1 << DmacChanSbcRx) | (1 << DmacChanSbcTx))) == 0;
#	endif
}

#  endif

// Set up the transmit DMA but don't enable it
static void SpiTxDmaSetup(const void* outBuffer, size_t bytesToTransfer) noexcept
	pre(bytesToTransfer <= outBuffer.limit)
{
#  if USE_DMAC
	DMAC->DMAC_EBCISR; // clear any pending interrupts

	dmac_channel_set_source_addr(DMAC, DmacChanSbcTx, reinterpret_cast<uint32_t>(outBuffer));
	dmac_channel_set_destination_addr(DMAC, DmacChanSbcTx, reinterpret_cast<uint32_t>(&(SBC_SPI->SPI_TDR)));
	dmac_channel_set_descriptor_addr(DMAC, DmacChanSbcTx, 0);
	dmac_channel_set_ctrlA(
		DMAC, DmacChanSbcTx, bytesToTransfer | DMAC_CTRLA_SRC_WIDTH_WORD | DMAC_CTRLA_DST_WIDTH_BYTE);
	dmac_channel_set_ctrlB(DMAC,
						   DmacChanSbcTx,
						   DMAC_CTRLB_SRC_DSCR | DMAC_CTRLB_DST_DSCR | DMAC_CTRLB_FC_MEM2PER_DMA_FC |
							   DMAC_CTRLB_SRC_INCR_INCREMENTING | DMAC_CTRLB_DST_INCR_FIXED);
#  endif

#  if USE_XDMAC
	xdmacTxCfg.mbr_ubc = bytesToTransfer;
	xdmacTxCfg.mbr_sa = (uint32_t)outBuffer;
	xdmacTxCfg.mbr_da = (uint32_t) & (SBC_SPI->SPI_TDR);
	xdmacTxCfg.mbr_cfg = XDMAC_CC_TYPE_PER_TRAN | XDMAC_CC_MBSIZE_SINGLE | XDMAC_CC_DSYNC_MEM2PER |
						 XDMAC_CC_CSIZE_CHK_1 | XDMAC_CC_DWIDTH_BYTE | XDMAC_CC_SIF_AHB_IF0 | XDMAC_CC_DIF_AHB_IF1 |
						 XDMAC_CC_SAM_INCREMENTED_AM | XDMAC_CC_DAM_FIXED_AM | XDMAC_CC_PERID(sbcSpiTxPerid);
	xdmacTxCfg.mbr_bc = 0;
	xdmacTxCfg.mbr_ds = 0;
	xdmacTxCfg.mbr_sus = 0;
	xdmacTxCfg.mbr_dus = 0;
	xdmac_configure_transfer(XDMAC, DmacChanSbcTx, &xdmacTxCfg);

	xdmac_channel_set_descriptor_control(XDMAC, DmacChanSbcTx, 0);
	xdmac_disable_interrupt(XDMAC, DmacChanSbcTx);
#  endif

#  if USE_DMAC_MANAGER
	DmacManager::SetSourceAddress(DmacChanSbcTx, outBuffer);
	DmacManager::SetDestinationAddress(DmacChanSbcTx, &(SbcSpiSercom->SPI.DATA.reg));
	DmacManager::SetBtctrl(DmacChanSbcTx,
						   DMAC_BTCTRL_STEPSIZE_X1 | DMAC_BTCTRL_STEPSEL_SRC | DMAC_BTCTRL_SRCINC |
							   DMAC_BTCTRL_BEATSIZE_WORD | DMAC_BTCTRL_BLOCKACT_NOACT);
	DmacManager::SetDataLength(DmacChanSbcTx, (bytesToTransfer + 3) >> 2); // must do this one last
	DmacManager::SetTriggerSourceSercomTx(DmacChanSbcTx, SbcSpiSercomNumber);
#  endif
}

// Set up the receive DMA but don't enable it
static void SpiRxDmaSetup(void* inBuffer, size_t bytesToTransfer) noexcept pre(bytesToTransfer <= inBuffer.limit)
{
#  if USE_DMAC
	DMAC->DMAC_EBCISR; // clear any pending interrupts

	dmac_channel_set_source_addr(DMAC, DmacChanSbcRx, reinterpret_cast<uint32_t>(&(SBC_SPI->SPI_RDR)));
	dmac_channel_set_destination_addr(DMAC, DmacChanSbcRx, reinterpret_cast<uint32_t>(inBuffer));
	dmac_channel_set_descriptor_addr(DMAC, DmacChanSbcRx, 0);
	dmac_channel_set_ctrlA(
		DMAC, DmacChanSbcRx, bytesToTransfer | DMAC_CTRLA_SRC_WIDTH_BYTE | DMAC_CTRLA_DST_WIDTH_WORD);
	dmac_channel_set_ctrlB(DMAC,
						   DmacChanSbcRx,
						   DMAC_CTRLB_SRC_DSCR | DMAC_CTRLB_DST_DSCR | DMAC_CTRLB_FC_PER2MEM_DMA_FC |
							   DMAC_CTRLB_SRC_INCR_FIXED | DMAC_CTRLB_DST_INCR_INCREMENTING);
#  endif

#  if USE_XDMAC
	xdmacRxCfg.mbr_ubc = bytesToTransfer;
	xdmacRxCfg.mbr_da = (uint32_t)inBuffer;
	xdmacRxCfg.mbr_sa = (uint32_t) & (SBC_SPI->SPI_RDR);
	xdmacRxCfg.mbr_cfg = XDMAC_CC_TYPE_PER_TRAN | XDMAC_CC_MBSIZE_SINGLE | XDMAC_CC_DSYNC_PER2MEM |
						 XDMAC_CC_CSIZE_CHK_1 | XDMAC_CC_DWIDTH_BYTE | XDMAC_CC_SIF_AHB_IF1 | XDMAC_CC_DIF_AHB_IF0 |
						 XDMAC_CC_SAM_FIXED_AM | XDMAC_CC_DAM_INCREMENTED_AM | XDMAC_CC_PERID(sbcSpiRxPerid);
	xdmacRxCfg.mbr_bc = 0;
	xdmacTxCfg.mbr_ds = 0;
	xdmacRxCfg.mbr_sus = 0;
	xdmacRxCfg.mbr_dus = 0;
	xdmac_configure_transfer(XDMAC, DmacChanSbcRx, &xdmacRxCfg);

	xdmac_channel_set_descriptor_control(XDMAC, DmacChanSbcRx, 0);
	xdmac_disable_interrupt(XDMAC, DmacChanSbcRx);
#  endif

#  if USE_DMAC_MANAGER
	DmacManager::SetSourceAddress(DmacChanSbcRx, &(SbcSpiSercom->SPI.DATA.reg));
	DmacManager::SetDestinationAddress(DmacChanSbcRx, inBuffer);
	DmacManager::SetBtctrl(DmacChanSbcRx,
						   DMAC_BTCTRL_STEPSIZE_X1 | DMAC_BTCTRL_STEPSEL_DST | DMAC_BTCTRL_DSTINC |
							   DMAC_BTCTRL_BEATSIZE_WORD | DMAC_BTCTRL_BLOCKACT_INT);
	DmacManager::SetDataLength(DmacChanSbcRx, (bytesToTransfer + 3) >> 2); // must do this one last
	DmacManager::SetTriggerSourceSercomRx(DmacChanSbcRx, SbcSpiSercomNumber);
#  endif
}

/**
 * \brief Set SPI slave transfer.
 */
static void SpiSlaveDmaSetup(void* inBuffer, const void* outBuffer, size_t bytesToTransfer) noexcept
	pre(bytesToTransfer <= inBuffer.limit; bytesToTransfer <= outBuffer.limit)
{
	SpiDmaDisable();
	SpiTxDmaSetup(outBuffer, bytesToTransfer);
	SpiRxDmaSetup(inBuffer, bytesToTransfer);

#  if USE_DMAC
	dmac_channel_enable(DMAC, DmacChanSbcRx);
	dmac_channel_enable(DMAC, DmacChanSbcTx);
#  endif

#  if USE_XDMAC
	xdmac_channel_enable(XDMAC, DmacChanSbcRx);
	xdmac_channel_enable(XDMAC, DmacChanSbcTx);
#  endif

#  if USE_DMAC_MANAGER
	DmacManager::EnableChannel(DmacChanSbcRx, DmacPrioSbc);
	DmacManager::EnableChannel(DmacChanSbcTx, DmacPrioSbc);
#  endif
}

void DisableSpi() noexcept
{
	// SPI/DMA is no longer armed, so the SBC must not initiate a transfer
	digitalWrite(SbcTfrReadyPin,
				 LOW); // interrupt should have already set this low, but just in case the interrupt hasn't fired yet

	// Sample how much of the armed length went unreceived before the channels are disabled, which
	// discards the count
	spiRxResidual = SpiDmaGetRxResidual();

	SpiDmaDisable();

#  if SAME5x
	SbcSpiSercom->SPI.CTRLA.reg &= ~SERCOM_SPI_CTRLA_ENABLE;
	while (SbcSpiSercom->SPI.SYNCBUSY.reg & (SERCOM_SPI_SYNCBUSY_SWRST | SERCOM_SPI_SYNCBUSY_ENABLE))
	{
	};
#  else
	spi_disable(SBC_SPI);
#  endif
}

static void SetupSpi(void* inBuffer, const void* outBuffer, size_t bytesToTransfer) noexcept
	pre(bytesToTransfer <= inBuffer.limit; bytesToTransfer <= outBuffer.limit)
{
	// Remember what this sub-exchange expects so that DoTransfer can tell whether the SBC clocked all of
	// it. Cleared here rather than after use so that a stale count can never be read as a fresh one
	spiArmedLength = bytesToTransfer;
	spiRxResidual = 0;

#  if !SAME5x
	// Reset SPI
	spi_reset(SBC_SPI);
	spi_set_slave_mode(SBC_SPI);
	spi_disable_mode_fault_detect(SBC_SPI);
	spi_set_peripheral_chip_select_value(SBC_SPI, spi_get_pcs(0));
	spi_set_clock_polarity(SBC_SPI, 0, 0);
	spi_set_clock_phase(SBC_SPI, 0, 1);
	spi_set_bits_per_transfer(SBC_SPI, 0, SPI_CSR_BITS_8_BIT);
#  endif

	// Initialize channel config for transmitter and receiver
	spiRxBuffer = inBuffer;
	spiTxBuffer = outBuffer;
	SpiSlaveDmaSetup(inBuffer, outBuffer, bytesToTransfer);

#  if USE_DMAC
	// Configure DMA RX channel
	dmac_channel_set_configuration(DMAC,
								   DmacChanSbcRx,
								   DMAC_CFG_SRC_PER(SBC_SPI_RX_DMA_HW_ID) | DMAC_CFG_SRC_H2SEL | DMAC_CFG_SOD |
									   DMAC_CFG_FIFOCFG_ASAP_CFG);

	// Configure DMA TX channel
	dmac_channel_set_configuration(DMAC,
								   DmacChanSbcTx,
								   DMAC_CFG_DST_PER(SBC_SPI_TX_DMA_HW_ID) | DMAC_CFG_DST_H2SEL | DMAC_CFG_SOD |
									   DMAC_CFG_FIFOCFG_ASAP_CFG);
#  endif

	// Enable SPI and notify the SBC we are ready
#  if SAME5x
	SbcSpiSercom->SPI.INTFLAG.reg = 0xFF;					  // clear any pending interrupts
	SbcSpiSercom->SPI.INTENSET.reg = SERCOM_SPI_INTENSET_TXC; // enable the end of transfer interrupt
	SbcSpiSercom->SPI.CTRLA.reg |= SERCOM_SPI_CTRLA_ENABLE;
	while (SbcSpiSercom->SPI.SYNCBUSY.reg & (SERCOM_SPI_SYNCBUSY_SWRST | SERCOM_SPI_SYNCBUSY_ENABLE))
	{
	};
#  else
	spi_enable(SBC_SPI);

	// Enable transfer-start and end-of-transfer interrupts
	(void)SBC_SPI->SPI_SR; // clear any pending interrupt
	SBC_SPI->SPI_IER = SPI_IER_TDRE | SPI_IER_NSSR;
	spiTransferStarted = false;
#  endif

	NVIC_SetPriority(SBC_SPI_IRQn, NvicPrioritySpi);
	NVIC_EnableIRQ(SBC_SPI_IRQn);

	// SPI/DMA is now armed, so the SBC may initiate a transfer
	digitalWrite(SbcTfrReadyPin, HIGH);
}

#  if SAME5x
void SbcSpiHandler(void*) noexcept
#  else
#	ifndef SBC_SPI_HANDLER
#	  error SBC_SPI_HANDLER undefined
#	endif

extern "C" void SBC_SPI_HANDLER() noexcept
#  endif
{
#  if SAME5x
	const uint8_t status = SbcSpiSercom->SPI.INTFLAG.reg;
	if ((status & SERCOM_SPI_INTFLAG_TXC) != 0)
	{
		SbcSpiSercom->SPI.INTENCLR.reg = SERCOM_SPI_INTENCLR_TXC; // disable the interrupt
		SbcSpiSercom->SPI.INTFLAG.reg = SERCOM_SPI_INTFLAG_TXC;	  // clear the status

		// SPI is no longer idle-armed once the SBC has initiated this transfer.
		digitalWrite(SbcTfrReadyPin, LOW);

		// Wake up the SBC task
		dataReceived = true;
		TaskBase::GiveFromISR(sbcTaskHandle, NotifyIndices::SbcInterface);
	}
#  else
	const uint32_t status = SBC_SPI->SPI_SR; // read status and clear interrupt
	const bool csLow = !digitalRead(APIN_SBC_SPI_SS0);

	if ((status & SPI_SR_TDRE) != 0 && csLow && !spiTransferStarted)
	{
		// TDRE while CS is asserted means the transfer has started.
		digitalWrite(SbcTfrReadyPin, LOW);
		spiTransferStarted = true;
	}

	if ((status & SPI_SR_NSSR) != 0)
	{
		SBC_SPI->SPI_IDR = SPI_IDR_TDRE | SPI_IDR_NSSR; // disable transfer-start/end interrupts
		spiTransferStarted = false;

		// Data has been transferred, disable transfer ready pin and XDMAC channels
		DisableSpi();

		// Check if any error occurred
		if ((status & SPI_SR_OVRES) != 0)
		{
			++spiRxOverruns;
		}
		if ((status & SPI_SR_UNDES) != 0)
		{
			++spiTxUnderruns;
		}

		// Wake up the SBC task
		dataReceived = true;
		TaskBase::GiveFromISR(sbcTaskHandle, NotifyIndices::SbcInterface);
	}
#  endif
}

/*-----------------------------------------------------------------------------------*/

// Static data. Note, the startup code we use doesn't make any provision for initialising non-cached memory, other than
// to zero. So don't specify initial value here

#  if SAME70
__nocache SpiTransferHeader DataTransfer::rxHeader;
__nocache SpiTransferHeader DataTransfer::txHeader;
__nocache uint32_t DataTransfer::rxResponse;
__nocache uint32_t DataTransfer::txResponse;
__nocache uint32_t DataTransfer::rxBufferMem[(SbcTransferBufferSize + 3) / 4];
__nocache uint32_t DataTransfer::txBufferMem[(SbcTransferBufferSize + 3) / 4];
#  endif

DataTransfer::DataTransfer() noexcept
	: m_state(InternalTransferState::ExchangingData)
	, m_lastTransferNumber(0)
	, m_failedTransfers(0)
	, m_checksumErrors(0)
	, m_dataResendAttempts(0)
	, m_shortTransfers(0)
	,
#  if SAME5x
	rxBuffer(nullptr)
	, txBuffer(nullptr)
	,
#  endif
	m_rxPointer(0)
	, m_txPointer(0)
	, m_transportType(SbcTransportType::spi)
	,
#  if SUPPORTS_SBC_OVER_USB
	m_usbDevice(nullptr)
	, m_usbDeviceIndex(0)
	,
#  endif
	m_packetId(0)
{
	rxResponse = SpiTransferResponse::Success;
	txResponse = SpiTransferResponse::Success;

	// Prepare RX header
	rxHeader.sequenceNumber = 0;

	// Prepare TX header
	txHeader.formatCode = SbcFormatCode;
	txHeader.protocolVersion = SbcProtocolVersion;
	txHeader.numPackets = 0;
	txHeader.sequenceNumber = 0;
}

void DataTransfer::Init() noexcept
{
	// Initialise transfer pins
	SetPinMode(SbcDataAvailablePin, OUTPUT_LOW);
	SetPinMode(SbcTfrReadyPin, OUTPUT_LOW);

	// Allocate the transfer buffers.
#  if SAME70
	// On the SAME70 the buffers must be in non-cached RAM because we DMA to/from them and the cache is write-back.
	// We use dedicated statically-reserved non-cached buffers (see the header).
	m_rxBuffer = (char*)rxBufferMem;
	m_txBuffer = (char*)txBufferMem;
#  else
	// The other processors we support have write-through cache, so ordinary heap memory is fine for DMA.
	rxBuffer = (char*)new uint32_t[(SbcTransferBufferSize + 3) / 4];
	txBuffer = (char*)new uint32_t[(SbcTransferBufferSize + 3) / 4];
#  endif

#  if SAME5x
	// Initialize SPI
	for (Pin p : SbcSpiSercomPins)
	{
		SetPinFunction(p, SbcSpiSercomPinsMode);
	}

	Serial::EnableSercomClock(SbcSpiSercomNumber);
	spi_dma_disable();

	SbcSpiSercom->SPI.CTRLA.reg |= SERCOM_SPI_CTRLA_SWRST;
	while (SbcSpiSercom->SPI.SYNCBUSY.reg & SERCOM_SPI_SYNCBUSY_SWRST)
	{
	};
	SbcSpiSercom->SPI.CTRLA.reg = SERCOM_SPI_CTRLA_DIPO(3) | SERCOM_SPI_CTRLA_DOPO(0) | SERCOM_SPI_CTRLA_MODE(2);
	SbcSpiSercom->SPI.CTRLB.reg = SERCOM_SPI_CTRLB_RXEN | SERCOM_SPI_CTRLB_SSDE | SERCOM_SPI_CTRLB_PLOADEN;
	while (SbcSpiSercom->SPI.SYNCBUSY.reg & SERCOM_SPI_SYNCBUSY_MASK)
	{
	};
	SbcSpiSercom->SPI.CTRLC.reg = SERCOM_SPI_CTRLC_DATA32B;
	Serial::SetSercomVector(SbcSpiSercomNumber, nullptr, SbcSpiHandler, nullptr, nullptr, nullptr);
#  else
	// Initialize SPI
	SetPinFunction(APIN_SBC_SPI_MOSI, SBCPinPeriphMode);
	SetPinFunction(APIN_SBC_SPI_MISO, SBCPinPeriphMode);
	SetPinFunction(APIN_SBC_SPI_SCK, SBCPinPeriphMode);
	SetPinFunction(APIN_SBC_SPI_SS0, SBCPinPeriphMode);
	SetDriveStrength(APIN_SBC_SPI_MISO, 3);

	spi_enable_clock(SBC_SPI);
	spi_disable(SBC_SPI);
#  endif

	dataReceived = false;

#  if false // if SAME70
	// This does not seem to change anything...
	// The XDMAC is master 4+5 and the SRAM is slave 0+1. Give the XDMAC the highest priority.
	matrix_set_slave_default_master_type(0, MATRIX_DEFMSTR_LAST_DEFAULT_MASTER);
	matrix_set_slave_priority(0, MATRIX_PRAS_M4PR(10));
	matrix_set_slave_priority(1, MATRIX_PRAS_M5PR(11));
	// Set the slave slot cycle limit.
	// If we leave it at the default value of 511 clock cycles, we get transmit underruns due to the HSMCI using the bus for too long.
	// A value of 8 seems to work. I haven't tried other values yet.
	matrix_set_slave_slot_cycle(0, 8);
	matrix_set_slave_slot_cycle(1, 8);
#  endif
#  if USE_DMAC
	pmc_enable_periph_clk(ID_DMAC);
	dmac_init(DMAC);
	dmac_set_priority_mode(DMAC, DMAC_PRIORITY_ROUND_ROBIN);
	dmac_enable(DMAC);

	// The DMAC is master 4 and the SRAM is slave 0. Give the DMAC the highest priority.
	matrix_set_slave_default_master_type(0, MATRIX_DEFMSTR_LAST_DEFAULT_MASTER);
	matrix_set_slave_priority(0, (3 << MATRIX_PRAS0_M4PR_Pos));
	// Set the slave slot cycle limit.
	// If we leave it at the default value of 511 clock cycles, we get transmit underruns due to the HSMCI using the bus
	// for too long. A value of 8 seems to work. I haven't tried other values yet.
	matrix_set_slave_slot_cycle(0, 8);
#  endif
}

#  if SUPPORTS_SBC_OVER_SPI

// Re-initialize SPI hardware after it was disabled for USB mode
void DataTransfer::ReinitSpi() noexcept
{
#	if SAME5x
	for (Pin p : SbcSpiSercomPins)
	{
		SetPinFunction(p, SbcSpiSercomPinsMode);
	}

	Serial::EnableSercomClock(SbcSpiSercomNumber);
	spi_dma_disable();

	SbcSpiSercom->SPI.CTRLA.reg |= SERCOM_SPI_CTRLA_SWRST;
	while (SbcSpiSercom->SPI.SYNCBUSY.reg & SERCOM_SPI_SYNCBUSY_SWRST)
	{
	};
	SbcSpiSercom->SPI.CTRLA.reg = SERCOM_SPI_CTRLA_DIPO(3) | SERCOM_SPI_CTRLA_DOPO(0) | SERCOM_SPI_CTRLA_MODE(2);
	SbcSpiSercom->SPI.CTRLB.reg = SERCOM_SPI_CTRLB_RXEN | SERCOM_SPI_CTRLB_SSDE | SERCOM_SPI_CTRLB_PLOADEN;
	while (SbcSpiSercom->SPI.SYNCBUSY.reg & SERCOM_SPI_SYNCBUSY_MASK)
	{
	};
	SbcSpiSercom->SPI.CTRLC.reg = SERCOM_SPI_CTRLC_DATA32B;
#	else
	SetPinFunction(APIN_SBC_SPI_MOSI, SBCPinPeriphMode);
	SetPinFunction(APIN_SBC_SPI_MISO, SBCPinPeriphMode);
	SetPinFunction(APIN_SBC_SPI_SCK, SBCPinPeriphMode);
	SetPinFunction(APIN_SBC_SPI_SS0, SBCPinPeriphMode);

	spi_enable_clock(SBC_SPI);
	spi_disable(SBC_SPI);
#	endif

	dataReceived = false;
}

#  endif // SUPPORTS_SBC_OVER_SPI

void DataTransfer::InitFromTask() noexcept
{
	sbcTaskHandle = TaskBase::GetCallerTaskHandle();
}

void DataTransfer::Diagnostics(const StringRef& reply) noexcept
{
#  if SUPPORTS_SBC_OVER_USB
	if (m_transportType == SbcTransportType::Usb)
	{
		reply.lcatf("Connected over USB (channel %u)", m_usbDeviceIndex);
	}
	else
#  endif
	{
		reply.lcat("Connected over SPI");
		reply.lcatf("Transfer state: %d, failed transfers: %u, checksum errors: %u",
					(int)m_state,
					m_failedTransfers,
					m_checksumErrors);
		reply.lcatf("RX/TX seq numbers: %d/%d", (int)rxHeader.sequenceNumber, (int)txHeader.sequenceNumber);
		reply.lcatf("SPI underruns %u, overruns %u, short transfers %u",
					spiTxUnderruns.load(),
					spiRxOverruns.load(),
					m_shortTransfers);
	}
}

bool DataTransfer::DataReceived() noexcept
{
	return dataReceived;
}

const PacketHeader* DataTransfer::ReadPacket() noexcept
{
	size_t rxDataLength = 0;
#  if SUPPORTS_SBC_OVER_USB
	if (m_transportType == SbcTransportType::Usb)
	{
		rxDataLength = m_usbRxHeader.dataLength;
	}
	else
#  endif
	{
		rxDataLength = rxHeader.dataLength;
	}

	if (m_rxPointer >= rxDataLength)
	{
		return nullptr;
	}

	const auto* header = reinterpret_cast<const PacketHeader*>(m_rxBuffer + m_rxPointer);
	m_rxPointer += sizeof(PacketHeader);
	return header;
}

const char* DataTransfer::ReadData(size_t dataLength) noexcept
{
	const char* data = m_rxBuffer + m_rxPointer;
	m_rxPointer += AddPadding(dataLength);
	return data;
}

template <typename T>
const T* DataTransfer::ReadDataHeader() noexcept
{
	const T* header = reinterpret_cast<const T*>(m_rxBuffer + m_rxPointer);
	m_rxPointer += sizeof(T);
	return header;
}

// Explicit instantiation so SbcInterface can read these headers (the template is defined in this translation unit)
template const CANRequestHeader* DataTransfer::ReadDataHeader<CANRequestHeader>() noexcept;
template const EnableCANHeader* DataTransfer::ReadDataHeader<EnableCANHeader>() noexcept;
template const ScheduleMoveHeader* DataTransfer::ReadDataHeader<ScheduleMoveHeader>() noexcept;

bool DataTransfer::ReadBoolean() noexcept
{
	const auto* header = ReadDataHeader<BooleanHeader>();
	return header->value;
}

bool DataTransfer::ReadMessage(MessageType& type, OutputBuffer* buf) noexcept
{
	// Read header
	const auto* header = ReadDataHeader<MessageHeader>();
	type = (MessageType)header->messageType;

	// Read message data and check if the it could be fully read
	const char* messageData = ReadData(header->length);
	return buf->Copy(messageData, header->length) == header->length;
}

void DataTransfer::ExchangeHeader() noexcept
{
	Cache::FlushBeforeDMASend(&txHeader, sizeof(txHeader));
	m_state = InternalTransferState::ExchangingHeader;
	m_dataResendAttempts = 0;
	SetupSpi(&rxHeader, &txHeader, sizeof(SpiTransferHeader));
}

void DataTransfer::ExchangeResponse(uint32_t response) noexcept
{
	txResponse = response;
	Cache::FlushBeforeDMASend(&txResponse, sizeof(txResponse));
	m_state = (m_state == InternalTransferState::ExchangingHeader) ? InternalTransferState::ExchangingHeaderResponse
																   : InternalTransferState::ExchangingDataResponse;
	SetupSpi(&rxResponse, &txResponse, sizeof(uint32_t));
}

void DataTransfer::ExchangeData() noexcept
{
	Cache::FlushBeforeDMASend(m_txBuffer, txHeader.dataLength);
	const auto bytesToExchange = max<size_t>(rxHeader.dataLength, txHeader.dataLength);
	m_state = InternalTransferState::ExchangingData;
	SetupSpi(m_rxBuffer, m_txBuffer, bytesToExchange);
}

void DataTransfer::RestartTransfer(bool ownRequest) noexcept
{
	if (reprap.Debug(Module::SbcInterface))
	{
		debugPrintf(ownRequest ? "Resetting transfer\n" : "Resetting transfer due to Sbc request\n");
	}

	m_failedTransfers++;
	if (ownRequest)
	{
		// Transfer bad data response and restart the transfer
		txResponse = SpiTransferResponse::BadResponse;
		Cache::FlushBeforeDMASend(&txResponse, sizeof(txResponse));
		m_state = InternalTransferState::Resetting;
		SetupSpi(&rxResponse, &txResponse, sizeof(uint32_t));
	}
	else
	{
		// The SBC sent BadResponse, which always means "abandon this transfer and start over". Restart
		// unconditionally so that both sides end up exchanging a header.
		//
		// This used to answer Success and wait in ExchangingDataResponseRetry when we were already
		// exchanging a header, to let an SBC that missed our data response see it after all. That
		// recovery only worked while the two sides stayed in phase: if the SBC had moved on it would be
		// clocking a 16-byte header against our 4-byte response, and the two would oscillate rather than
		// converge. Restarting costs one transfer but always re-synchronises
		ExchangeHeader();
	}
}

// Bound on how many times we resend the data of a single transfer after a checksum error. The SBC gives
// up after MaxSbcRetries (3 by default) and resets the connection, so without a bound of our own we would
// keep re-arming the data exchange for a peer that has already gone away. Kept above the SBC's limit so
// that we never cut its retries short
static constexpr unsigned int maxDataResendAttempts = 5;

TransferState DataTransfer::DoTransfer() noexcept
{
#  if SUPPORTS_SBC_OVER_USB
	if (m_transportType == SbcTransportType::Usb)
	{
		return DoTransferUsb();
	}
#  endif

	if (dataReceived)
	{
#  if SAME5x
		if (SbcSpiSercom->SPI.STATUS.bit.BUFOVF)
		{
			++spiRxOverruns;
		}
		disable_spi();
#  else
		// Wait for the current XDMA transfer to finish. Relying on the XDMAC IRQ for this is does not work well...
		if (!SpiDmaCheckRxComplete())
		{
			return TransferState::FinishingTransfer;
		}
#  endif

		// Transfer has finished
		dataReceived = false;

		// The SBC clocked fewer bytes than this sub-exchange was armed for, so the two sides disagree
		// about which sub-exchange it was - typically because the SBC read a stale transfer ready level
		// and started before we re-armed, or because it is a phase ahead. Whatever the DMA did receive is
		// short by the difference and the rest of the buffer is left over from the previous exchange, so
		// there is nothing here worth parsing. Restart instead of guessing
		if (spiRxResidual != 0 && m_state != InternalTransferState::ProcessingData &&
			m_state != InternalTransferState::Resetting)
		{
			if (reprap.Debug(Module::SbcInterface))
			{
				debugPrintf("Short SPI transfer (%u of %u bytes), restarting\n",
							(unsigned int)(spiArmedLength - spiRxResidual),
							(unsigned int)spiArmedLength);
			}
			++m_shortTransfers;
			// If the SBC sent `BadResponse` in the just completed transfer then it is already expecting a restart, so
			// don't send another one
			Cache::InvalidateAfterDMAReceive(spiRxBuffer, spiArmedLength - spiRxResidual);
			const uint32_t response = *reinterpret_cast<const volatile uint32_t*>(spiRxBuffer);
			RestartTransfer(response != SpiTransferResponse::BadResponse);
			return TransferState::DoingPartialTransfer;
		}

		switch (m_state)
		{
		case InternalTransferState::ExchangingHeader:
		{
			// (1) Exchanged transfer headers
			Cache::InvalidateAfterDMAReceive(&rxHeader, sizeof(rxHeader));
			const uint32_t headerResponse = *reinterpret_cast<const uint32_t*>(&rxHeader);
			if (headerResponse == SpiTransferResponse::BadResponse)
			{
				// The SBC abandoned the transfer and wants to start over, so re-arm the header exchange
				if (reprap.Debug(Module::SbcInterface))
				{
					debugPrintf("Restarting transfer at Sbc request\n");
				}
				RestartTransfer(false);
				break;
			}

			const uint32_t checksum =
				CalcCRC32(reinterpret_cast<const char*>(&rxHeader), SbcProtocol::SpiTransferHeaderCrcLength);
			if (rxHeader.crcHeader != checksum)
			{
				if (reprap.Debug(Module::SbcInterface))
				{
					debugPrintf(
						"Bad header CRC (expected %08" PRIx32 ", got %08" PRIx32 ")\n", rxHeader.crcHeader, checksum);
				}
				ExchangeResponse(SpiTransferResponse::BadHeaderChecksum);
				break;
			}

			if (rxHeader.formatCode != SbcFormatCode)
			{
				ExchangeResponse(SpiTransferResponse::BadFormat);
				break;
			}
			if (rxHeader.protocolVersion != SbcProtocolVersion)
			{
				ExchangeResponse(SpiTransferResponse::BadProtocolVersion);
				break;
			}
			if (rxHeader.dataLength > SbcTransferBufferSize)
			{
				ExchangeResponse(SpiTransferResponse::BadDataLength);
				break;
			}

			ExchangeResponse(SpiTransferResponse::Success);
			break;
		}

		case InternalTransferState::ExchangingHeaderResponse:
			// (2) Exchanged response to transfer header
			Cache::InvalidateAfterDMAReceive(&rxResponse, sizeof(rxResponse));
			if (rxResponse == SpiTransferResponse::Success && txResponse == SpiTransferResponse::Success)
			{
				if (rxHeader.dataLength != 0 || txHeader.dataLength != 0)
				{
					// Perform the actual data transfer
					ExchangeData();
				}
				else
				{
					// Everything OK
					m_rxPointer = m_txPointer = 0;
					m_packetId = 0;
					m_state = InternalTransferState::ProcessingData;
					return IsConnectionReset() ? TransferState::ConnectionReset : TransferState::Finished;
				}
			}
			else if (rxResponse == SpiTransferResponse::BadHeaderChecksum ||
					 txResponse == SpiTransferResponse::BadHeaderChecksum)
			{
				// Failed to exchange header, restart the full transfer
				m_checksumErrors++;
				ExchangeHeader();
			}
			else
			{
				// Restart the full transfer
				RestartTransfer(rxResponse != SpiTransferResponse::BadResponse);
			}
			break;

		case InternalTransferState::ExchangingData:
		{
			// (3) Exchanged data
			Cache::InvalidateAfterDMAReceive(m_rxBuffer, rxHeader.dataLength);
			if (*reinterpret_cast<uint32_t*>(m_rxBuffer) == SpiTransferResponse::BadResponse)
			{
				RestartTransfer(false);
				break;
			}

			const uint32_t checksum = CalcCRC32(m_rxBuffer, rxHeader.dataLength);
			if (rxHeader.crcData != checksum)
			{
				if (reprap.Debug(Module::SbcInterface))
				{
					debugPrintf(
						"Bad data CRC (expected %08" PRIx32 ", got %08" PRIx32 ")\n", rxHeader.crcData, checksum);
				}
				ExchangeResponse(SpiTransferResponse::BadDataChecksum);
				break;
			}

			ExchangeResponse(SpiTransferResponse::Success);
			break;
		}

		case InternalTransferState::ExchangingDataResponse:
			// (4a) Exchanged response to data transfer
			Cache::InvalidateAfterDMAReceive(&rxResponse, sizeof(rxResponse));
			if (rxResponse == SpiTransferResponse::Success && txResponse == SpiTransferResponse::Success)
			{
				// Everything OK
				m_rxPointer = m_txPointer = 0;
				m_packetId = 0;
				m_state = InternalTransferState::ProcessingData;
				return IsConnectionReset() ? TransferState::ConnectionReset : TransferState::Finished;
			}

			if (rxResponse == SpiTransferResponse::BadDataChecksum ||
				txResponse == SpiTransferResponse::BadDataChecksum)
			{
				// Resend the data if a checksum error occurred
				m_checksumErrors++;
				if (++m_dataResendAttempts > maxDataResendAttempts)
				{
					// The data is not getting through. Resending it again would loop until the SBC times
					// out, so give up and let the connection be re-established
					if (reprap.Debug(Module::SbcInterface))
					{
						debugPrintf("Too many data resend attempts, resetting connection\n");
					}
					return TransferState::ConnectionReset;
				}
				ExchangeData();
			}
			else if (rxResponse == SpiTransferResponse::BadResponse)
			{
				// Restart the full transfer
				RestartTransfer(false);
			}
			else
			{
				// We are about to send BadResponse, so restart the whole transfer rather than coming back
				// to the data response exchange. Both sides treat BadResponse as "start over from a
				// header", so anything else would leave us exchanging responses against the SBC's header
				RestartTransfer(true);
			}
			break;

		case InternalTransferState::Resetting:
			// Transmitted bad response, attempt to restart the connection
			ExchangeHeader();
			break;

		default:
			// Should never get here. If we do, this probably means that StartNextTransfer has not been called
			ExchangeHeader();
			REPORT_INTERNAL_ERROR;
			break;
		}
	}
	return (m_state == InternalTransferState::ExchangingHeader) ? TransferState::DoingFullTransfer
																: TransferState::DoingPartialTransfer;
}

#  if SUPPORTS_SBC_OVER_USB

void DataTransfer::SwitchToUsb(SerialCDC* dev, unsigned int devIndex) noexcept
{
	DisableSpi();
	m_transportType = SbcTransportType::Usb;
	m_usbDevice = dev;
	m_usbDeviceIndex = devIndex;
	m_rxPointer = m_txPointer = 0;
	m_packetId = 0;
	memset(&m_usbRxHeader, 0, sizeof(m_usbRxHeader));
	memset(&m_usbTxHeader, 0, sizeof(m_usbTxHeader));
}

static constexpr uint32_t usbTimeoutMs =
	SbcConnectionTimeout; // must be long enough for DSF to process between transfers

TransferState DataTransfer::DoTransferUsb() noexcept
{
	// USB uses request-response protocol with zero-copy direct endpoint access
	// BeginDirectMode was called during SBC activation, so we use readDirect/writeDirect
	// DSF writes first, RRF reads then responds

	// 1) Read DSF's header (wait for DSF to initiate the transfer)
	const size_t hdrBytes =
		m_usbDevice->readDirect(reinterpret_cast<uint8_t*>(&m_usbRxHeader), sizeof(UsbTransferHeader), usbTimeoutMs);
	if (hdrBytes != sizeof(UsbTransferHeader))
	{
		if (reprap.Debug(Module::SbcInterface))
		{
			debugPrintf("USB: readDirect header got %u bytes\n", (unsigned)hdrBytes);
		}
		return TransferState::ConnectionTimeout;
	}

	// 2) Write our header in response
	m_usbTxHeader.numPackets = m_packetId;
	m_usbTxHeader.dataLength = (uint16_t)m_txPointer;
	if (!m_usbDevice->writeDirect(
			reinterpret_cast<const uint8_t*>(&m_usbTxHeader), sizeof(UsbTransferHeader), usbTimeoutMs))
	{
		return TransferState::ConnectionTimeout;
	}

	// Validate data length
	if (m_usbRxHeader.dataLength > SbcTransferBufferSize)
	{
		return TransferState::ConnectionReset;
	}

	// 3) Read DSF's data body (DSF writes first)
	if (m_usbRxHeader.dataLength > 0)
	{
		if (m_usbDevice->readDirect(reinterpret_cast<uint8_t*>(m_rxBuffer), m_usbRxHeader.dataLength, usbTimeoutMs) !=
			m_usbRxHeader.dataLength)
		{
			return TransferState::ConnectionTimeout;
		}
	}

	// 4) Write our data body in response
	if (m_txPointer > 0)
	{
		if (!m_usbDevice->writeDirect(reinterpret_cast<const uint8_t*>(m_txBuffer), m_txPointer, usbTimeoutMs))
		{
			return TransferState::ConnectionTimeout;
		}
	}

	// Reset pointers for next transfer
	m_rxPointer = m_txPointer = 0;
	m_packetId = 0;
	return TransferState::Finished;
}

#  endif // SUPPORTS_SBC_OVER_USB

void DataTransfer::StartNextTransfer(bool keepSequence) noexcept
{
#  if SUPPORTS_SBC_OVER_USB
	if (m_transportType == SbcTransportType::Usb)
	{
		// USB: only reset rxPointer. txPointer/packetId are set by ExchangeData
		// and must be preserved until DoTransferUsb sends them
		// DoTransferUsb resets txPointer/packetId after sending
		m_rxPointer = 0;
		return;
	}
#  endif

	if (keepSequence)
	{
		// Re-arming a transfer the SBC never clocked (new outgoing data arrived while idle-armed).
		// Tear down the stale armed DMA and keep the TX sequence number / RX tracking untouched, so
		// the SBC sees no sequence gap when it eventually clocks this transfer.
		DisableSpi();
	}
	else
	{
		m_lastTransferNumber = rxHeader.sequenceNumber;
	}

	// Reset RX transfer header
	rxHeader.formatCode = InvalidFormatCode;
	rxHeader.numPackets = 0;
	rxHeader.protocolVersion = 0;
	rxHeader.dataLength = 0;
	rxHeader.crcData = 0;
	rxHeader.crcHeader = 0;

	// Set up TX transfer header
	txHeader.numPackets = m_packetId;
	if (!keepSequence)
	{
		txHeader.sequenceNumber++;
	}
	txHeader.dataLength = m_txPointer;

	// Sampled here rather than when the transfer was assembled: this is the last thing done before
	// the exchange is armed, so the delay between the reading and the SBC receiving it is as near
	// constant as it can be. The SBC fits its step clock to these, and a varying delay is what that
	// fit cannot remove
	txHeader.masterClock = StepTimer::GetTimerTicks();
	txHeader.hiccupTime = StepTimer::GetMovementDelay();

	txHeader.crcData = CalcCRC32(m_txBuffer, m_txPointer);
	txHeader.crcHeader = CalcCRC32(reinterpret_cast<const char*>(&txHeader), SbcProtocol::SpiTransferHeaderCrcLength);

	// Tell the SBC whether this armed transfer carries outgoing data. When set, the SBC will clock a
	// transfer even if it has nothing of its own to send, so our data gets pulled promptly.
	digitalWrite(SbcDataAvailablePin, (m_txPointer > 0) ? HIGH : LOW);

	// Begin SPI transfer
	ExchangeHeader();
}

void DataTransfer::ResetConnection(bool fullReset) noexcept
{
#  if SUPPORTS_SBC_OVER_USB
	if (m_transportType == SbcTransportType::Usb)
	{
		m_usbDevice = nullptr;
		m_rxPointer = m_txPointer = 0;
		m_packetId = 0;

#	if SUPPORTS_SBC_OVER_SPI
		// Fall back to SPI: re-initialize the hardware that was disabled by SwitchToUsb()
		m_transportType = SbcTransportType::spi;
		ReinitSpi();
#	else
		// USB-only board: just reset and wait for a new M576.1
		return;
#	endif
	}
#  endif

	// Clear the remaining data to send
	DisableSpi();
	dataReceived = false;
	m_rxPointer = m_txPointer = 0;
	m_packetId = 0;

	// Nothing queued to send any more, so drop the data-available signal
	digitalWrite(SbcDataAvailablePin, LOW);

	// Reset the seq numbers only if no communication is taking place. The TfrReady pin was already
	// driven low by disable_spi() above and will go high again when StartNextTransfer() re-arms.
	if (fullReset)
	{
		m_lastTransferNumber = rxHeader.sequenceNumber = txHeader.sequenceNumber = 0;
	}

	// Kick off a new transfer
	StartNextTransfer();
}

bool DataTransfer::WriteCodeBufferUpdate(uint16_t bufferSpace) noexcept
{
	if (!CanWritePacket(sizeof(CodeBufferUpdateHeader)))
	{
		return false;
	}

	// Write packet header
	(void)WritePacketHeader(FirmwareRequest::CodeBufferUpdate, sizeof(CodeBufferUpdateHeader));

	// Write header
	auto* header = WriteDataHeader<CodeBufferUpdateHeader>();
	header->bufferSpace = bufferSpace;
	header->padding = 0;
	return true;
}

bool DataTransfer::WriteCodeReply(MessageType type, OutputBuffer*& response) noexcept
{
	// Try to write the packet header. This packet type can deal with truncated messages
	const auto minBytesToWrite = min<size_t>(16, (response == nullptr) ? 0 : response->Length());
	if (!CanWritePacket(sizeof(MessageHeader) + minBytesToWrite))
	{
		// Not enough space left
		return false;
	}

	// Write packet header
	PacketHeader* header = WritePacketHeader(FirmwareRequest::Message);

	// Write code reply header
	auto* replyHeader = WriteDataHeader<MessageHeader>();
	replyHeader->messageType = (uint32_t)type;
	replyHeader->padding = 0;

	// Write code reply
	size_t bytesWritten = 0;
	if (response != nullptr)
	{
		size_t bytesToCopy = 0;
		do
		{
			bytesToCopy = min<size_t>(FreeTxSpace(), response->BytesLeft());
			if (bytesToCopy == 0)
			{
				break;
			}

			WriteData(response->UnreadData(), bytesToCopy);
			bytesWritten += bytesToCopy;

			response->Taken(bytesToCopy);
			if (response->BytesLeft() == 0)
			{
				response = OutputBuffer::Release(response);
			}
		} while (response != nullptr);

		if (response != nullptr)
		{
			// There is more to come...
			replyHeader->messageType = replyHeader->messageType | (uint32_t)PushFlag;
		}
	}

	// Finish the packet
	replyHeader->length = bytesWritten;
	header->length = sizeof(MessageHeader) + bytesWritten;
	return true;
}

// Forward a received CAN message to the SBC. The payload is header.dataLength bytes.
// Returns false if there isn't enough room in this transfer, in which case the caller should try again next time.
bool DataTransfer::WriteCANResponse(const CANResponseHeader& header, const char* _ecv_null payload) noexcept
{
	if (!CanWritePacket(sizeof(CANResponseHeader) + header.dataLength))
	{
		return false;
	}

	// Write packet header
	(void)WritePacketHeader(FirmwareRequest::CANResponse, sizeof(CANResponseHeader) + header.dataLength);

	// Write the CAN response header
	auto* hdr = WriteDataHeader<CANResponseHeader>();
	*hdr = header;

	// Write the payload
	if (payload != nullptr && header.dataLength != 0)
	{
		WriteData(payload, header.dataLength);
	}
	return true;
}

// Tell the SBC which drives an endstop stopped and when it fired, so it can work out where they
// should end up and send the revert.
// Returns false if there isn't enough room in this transfer, in which case the caller should try again next time.
bool DataTransfer::WriteMotionStopped(const MotionStoppedHeader& header, const MotionStoppedDriver* drivers) noexcept
{
	const size_t driversBytes = header.numDrivers * sizeof(MotionStoppedDriver);
	if (!CanWritePacket(sizeof(MotionStoppedHeader) + driversBytes))
	{
		return false;
	}

	(void)WritePacketHeader(FirmwareRequest::MotionStopped, sizeof(MotionStoppedHeader) + driversBytes);

	auto* hdr = WriteDataHeader<MotionStoppedHeader>();
	*hdr = header;
	if (driversBytes != 0)
	{
		WriteData(reinterpret_cast<const char*>(drivers), driversBytes);
	}
	return true;
}

PacketHeader* DataTransfer::WritePacketHeader(FirmwareRequest request,
											  size_t dataLength,
											  uint16_t resendPacketId) noexcept
{
	// Make sure to stay aligned if the last packet ended with a string
	m_txPointer = AddPadding(m_txPointer);

	// Write the next packet data
	auto* header = reinterpret_cast<PacketHeader*>(m_txBuffer + m_txPointer);
	header->request = static_cast<uint16_t>(request);
	header->id = m_packetId++;
	header->length = dataLength;
	header->resendPacketId = resendPacketId;
	m_txPointer += sizeof(PacketHeader);
	return header;
}

void DataTransfer::WriteData(const char* data, size_t length) noexcept
{
	// Strings can be concatenated here, don't add any padding yet
	memcpy(m_txBuffer + m_txPointer, data, length);
	m_txPointer += length;
}

template <typename T>
T* DataTransfer::WriteDataHeader() noexcept
{
	T* header = reinterpret_cast<T*>(m_txBuffer + m_txPointer);
	m_txPointer += sizeof(T);
	return header;
}

uint32_t DataTransfer::CalcCRC32(const char* buffer, size_t length) noexcept
{
	CRC32 crc;
	crc.Update(buffer, length);
	return crc.Get();
}

#endif
