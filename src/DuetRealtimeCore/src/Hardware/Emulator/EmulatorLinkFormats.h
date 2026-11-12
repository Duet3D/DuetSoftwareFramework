// Wire format of the emulator link: the SBC's SPI bus and the controller's TfrRdy and DataAvailable
// lines, carried over a Unix domain stream socket to an emulated controller.
//
// The peer is the SbcPortBridge peripheral in Renode (src/DuetEmulation/Renode/Peripherals/SAME70/
// SAME70_SbcPortBridge.cs), which mirrors these definitions. Only the physical layer crosses the link:
// the transfer protocol on top of it is SpiTransfer's on this side and the firmware's on the other, so
// both run as they do on real hardware.
//
// Every frame starts with an EmulatorFrameHeader. Integers are little-endian.
//
//   Exchange        SBC -> emulator  The bytes the master clocks out (MOSI), one chip-select cycle.
//   ExchangeResult  emulator -> SBC  The bytes the slave clocked back (MISO), as many as were sent.
//   LineLevel       emulator -> SBC  An input line's level: payload is the line, then 0 or 1. The
//                                    emulator sends one for each line when the SBC connects and one
//                                    each time a line changes.
//
// Frames from the emulator are in the order things happened on the emulated controller, so a
// LineLevel that follows an ExchangeResult happened after that exchange.
#pragma once

#include <cstddef>
#include <cstdint>

namespace Duet::Sbc::Emulator
{

	enum class FrameType : uint8_t
	{
		Exchange = 1,
		ExchangeResult = 2,
		LineLevel = 3,
	};

	enum class Line : uint8_t
	{
		TransferReady = 0,
		DataAvailable = 1,
	};

	inline constexpr size_t NumLines = 2;

#pragma pack(push, 1)
	struct FrameHeader
	{
		uint8_t type; // FrameType
		uint8_t reserved[3];
		uint32_t length; // payload bytes that follow
	};

	struct LineLevelPayload
	{
		uint8_t line; // Line
		uint8_t level;
	};
#pragma pack(pop)

	static_assert(sizeof(FrameHeader) == 8, "FrameHeader must be 8 bytes");
	static_assert(sizeof(LineLevelPayload) == 2, "LineLevelPayload must be 2 bytes");

	// The longest exchange either side accepts. SpiTransfer's largest is one data block.
	inline constexpr uint32_t MaxExchangeLength = 65536;

} // namespace Duet::Sbc::Emulator
