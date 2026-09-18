/*
 * DataTransfer.h
 *
 *  Created on: 29 Mar 2019
 *      Author: Christian
 */

#ifndef SRC_SBC_DATATRANSFER_H_
#define SRC_SBC_DATATRANSFER_H_

#include "RepRapFirmware.h"

#if HAS_SBC_INTERFACE

#  include "SbcMessageFormats.h"
#  include <RTOSIface/RTOSIface.h>

class BinaryGCodeBuffer;
class SerialCDC;
class StringRef;
class OutputBuffer;
class GCodeMachineState;
class HeightMap;

struct ExpressionValue;

enum class TransferState : uint8_t
{
	DoingFullTransfer,
	DoingPartialTransfer,
	FinishingTransfer,
	ConnectionTimeout,
	ConnectionReset,
	Finished
};

class DataTransfer
{
  public:
	DataTransfer() noexcept;
	void Init() noexcept;
	static void InitFromTask() noexcept;
	void Diagnostics(const StringRef& reply) noexcept;

	[[nodiscard]] SbcTransportType GetTransportType() const noexcept { return m_transportType; }
#  if SUPPORTS_SBC_OVER_USB
	void SwitchToUsb(SerialCDC* dev, unsigned int devIndex) noexcept; // Switch from SPI to USB transport
	[[nodiscard]] SerialCDC* GetUsbDevice() const noexcept { return m_usbDevice; }
#  endif

	TransferState DoTransfer() noexcept; // Try to finish the current transfer
	static bool DataReceived() noexcept; // True if the SPI ISR has flagged transfer activity (vs. a task wake from new
										 // outgoing data)
	void StartNextTransfer(
		bool keepSequence = false) noexcept; // Kick off the next transfer (keepSequence re-arms an un-clocked transfer
											 // without advancing the sequence number)
	void ResetConnection(bool fullReset) noexcept; // Reset the connection after a longer timeout

	[[nodiscard]] size_t PacketsToRead() const noexcept;
	const PacketHeader* ReadPacket() noexcept; // Attempt to read the next packet header or return null. Advances the
											   // read pointer to the next packet or the packet's data
	const char* ReadData(size_t dataLength) noexcept; // Read the packet data and advance to the next packet (if any)
	bool ReadBoolean() noexcept;					  // Read a boolean value
	bool ReadMessage(MessageType& type, OutputBuffer* buf) noexcept; // Read a request to output a message
	template <typename T>
	const T* ReadDataHeader() noexcept; // Read a fixed-size data header without padding (caller then reads any trailing
										// data)

	void ResendPacket(const PacketHeader* packet) noexcept;
	bool WriteCodeBufferUpdate(uint16_t bufferSpace) noexcept;
	bool WriteCodeReply(MessageType type, OutputBuffer*& response) noexcept;
	bool WritePrintPaused(FilePosition position, FilePosition position2, PrintPausedReason reason) noexcept;
	bool WriteMotionStopped(const MotionStoppedHeader& header, const MotionStoppedDriver* drivers) noexcept;
	bool WriteCanMessagesSent(const CanMessageSentEntry* entries, size_t count) noexcept;
	bool WriteCANResponse(const CANResponseHeader& header,
						  const char* _ecv_null payload) noexcept; // Forward a received CAN message to the SBC

  private:
	// Both sides treat BadResponse as "abandon this transfer and start over from a header", so there is no
	// state for retrying a response exchange after one has been sent or received
	enum class InternalTransferState : uint8_t
	{
		ExchangingHeader,
		ExchangingHeaderResponse,
		ExchangingData,
		ExchangingDataResponse,
		ProcessingData,
		Resetting
	} m_state;

	// Transfer properties
	uint16_t m_lastTransferNumber;
	unsigned int m_failedTransfers, m_checksumErrors;
	unsigned int m_dataResendAttempts; // consecutive data resends within the current transfer, bounds the
									   // ExchangingDataResponse retry loop
	unsigned int m_shortTransfers;	   // sub-exchanges the SBC clocked fewer bytes of than we armed for

	// Transfer buffers
#  if SAME70
	// SAME70 has a write-back cache, so these must be in non-cached memory because we DMA to/from them.
	// See
	// http://ww1.microchip.com/downloads/en/DeviceDoc/Managing-Cache-Coherency-on-Cortex-M7-Based-MCUs-DS90003195A.pdf
	// This in turn means that we must declare them static, so we can only have one DataTransfer instance
	static __nocache SpiTransferHeader rxHeader;
	static __nocache SpiTransferHeader txHeader;
	static __nocache uint32_t rxResponse;
	static __nocache uint32_t txResponse;
	// The transfer buffers must also be in non-cached RAM. We reserve them statically rather than borrowing from a
	// networking buffer pool, because this firmware is a CAN/SBC bridge and does not run a wired Ethernet stack.
	static __nocache uint32_t rxBufferMem[(SbcTransferBufferSize + 3) / 4];
	static __nocache uint32_t txBufferMem[(SbcTransferBufferSize + 3) / 4];
#  else
	// The other processors we support have write-through cache
	// Allocate the buffers in the object so that we can delete the object and recycle the memory if the SBC interface
	// is not being used Align the headers on 16-byte boundaries so that they span only one cache line
	alignas(16) SpiTransferHeader rxHeader;
	alignas(16) SpiTransferHeader txHeader;
	uint32_t rxResponse;
	uint32_t txResponse;
#  endif
	char* m_rxBuffer{}; // not allocated until we know we need it
	char* m_txBuffer{}; // not allocated until we know we need it
	size_t m_rxPointer, m_txPointer;

	// Transport type
	SbcTransportType m_transportType;

#  if SUPPORTS_SBC_OVER_USB
	// USB transport members
	SerialCDC* m_usbDevice;
	unsigned int m_usbDeviceIndex;
	UsbTransferHeader m_usbRxHeader{};
	UsbTransferHeader m_usbTxHeader{};

	TransferState DoTransferUsb() noexcept;
#  endif

	// Packet properties
	uint16_t m_packetId;

	[[nodiscard]] bool IsConnectionReset() const noexcept;

	void ExchangeHeader() noexcept;
	void ExchangeResponse(uint32_t response) noexcept;
	void ExchangeData() noexcept;
	void RestartTransfer(bool ownRequest) noexcept;
	static uint32_t CalcCRC32(const char* buffer, size_t length) noexcept;

#  if SUPPORTS_SBC_OVER_SPI
	static void ReinitSpi() noexcept; // Re-initialize SPI hardware after USB mode
#  endif

	// Always keep enough tx space to allow resend requests in case RRF runs out of resources and cannot process an
	// incoming request right away
	[[nodiscard]] size_t FreeTxSpace() const noexcept;
	[[nodiscard]] uint8_t GetRxNumPackets() const noexcept;

	[[nodiscard]] bool CanWritePacket(size_t dataLength = 0) const noexcept;
	PacketHeader* WritePacketHeader(FirmwareRequest request,
									size_t dataLength = 0,
									uint16_t resendPacketId = 0) noexcept;
	void WriteData(const char* data, size_t length) noexcept;
	template <typename T>
	T* WriteDataHeader() noexcept;

	[[nodiscard]] static size_t AddPadding(size_t length) noexcept;
};

inline bool DataTransfer::IsConnectionReset() const noexcept
{
	const uint16_t nextTransferNumber = m_lastTransferNumber + 1u;
	return (rxHeader.formatCode == SbcFormatCode) && (rxHeader.sequenceNumber != nextTransferNumber);
}

inline uint8_t DataTransfer::GetRxNumPackets() const noexcept
{
#  if SUPPORTS_SBC_OVER_USB
	if (m_transportType == SbcTransportType::Usb)
	{
		return m_usbRxHeader.numPackets;
	}
#  endif
	return rxHeader.numPackets;
}

inline size_t DataTransfer::FreeTxSpace() const noexcept
{
	return SbcTransferBufferSize - AddPadding(m_txPointer) - GetRxNumPackets() * sizeof(PacketHeader);
}

inline size_t DataTransfer::PacketsToRead() const noexcept
{
	return GetRxNumPackets();
}

inline void DataTransfer::ResendPacket(const PacketHeader* packet) noexcept
{
	WritePacketHeader(FirmwareRequest::ResendPacket, 0, packet->id);
}

inline bool DataTransfer::CanWritePacket(size_t dataLength) const noexcept
{
	return FreeTxSpace() >= sizeof(PacketHeader) + dataLength;
}

inline size_t DataTransfer::AddPadding(size_t length) noexcept
{
	const size_t extraBytes = (length & 3);
	return (extraBytes == 0) ? length : length + 4 - extraBytes;
}
#endif // HAS_SBC_INTERFACE

#endif /* SRC_SBC_DATATRANSFER_H_ */
