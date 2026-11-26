// Wire format of the emulator link: the SBC's SPI bus and the controller's TfrRdy and DataAvailable
// lines, carried over a Unix domain stream socket to an emulated controller.
//
// The peer is the SBC link of the SPI model in Renode (src/DuetEmulation/Renode/Peripherals/SAME70/
// SAME70_SbcSpi.cs), which mirrors these definitions. Only the physical layer crosses the link: the
// transfer protocol on top of it is SpiTransfer's on this side and the firmware's on the other, so
// both run as they do on real hardware.
//
// Every frame starts with a FrameHeader. Integers are little-endian.
//
//   Exchange        SBC -> emulator  The bytes the master clocks out (MOSI), one chip-select cycle.
//   ExchangeResult  emulator -> SBC  The bytes the slave clocked back (MISO), as many as were sent,
//                                    with the Exchange's sequence number, so that the result of an
//                                    exchange the SBC gave up waiting for is not taken for a later one.
//   LineLevel       emulator -> SBC  An input line's level: payload is the line, then 0 or 1. The
//                                    emulator sends one for each line when the SBC connects and one
//                                    each time a line changes.
//
// The two sides run in lockstep under a bench that drives both clocks (src/DuetEmulation/Dsf). Each
// frame from the emulator carries the emulated time it was sent at, and the SBC holds it until the
// bench has moved the SBC's clock (Platform/LocalClock.h) to that time and releases it. The emulator
// applies the SBC's Exchanges only when the bench tells it to, between runs of emulated time, and
// says how many frames it has sent, so the bench knows when they have all arrived. Both directions
// therefore happen at the same emulated times on every run.
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
		uint8_t reserved;
		uint16_t sequence;		 // Exchange: numbered by the SBC; ExchangeResult: that of its Exchange
		uint32_t length;		 // payload bytes that follow
		uint64_t emulatedTimeNs; // emulator -> SBC: emulated time when sent; SBC -> emulator: 0
	};

	struct LineLevelPayload
	{
		uint8_t line; // Line
		uint8_t level;
	};
#pragma pack(pop)

	static_assert(sizeof(FrameHeader) == 16, "FrameHeader must be 16 bytes");
	static_assert(sizeof(LineLevelPayload) == 2, "LineLevelPayload must be 2 bytes");

	// The longest exchange either side accepts. SpiTransfer's largest is one data block.
	inline constexpr uint32_t MaxExchangeLength = 65536;

} // namespace Duet::Sbc::Emulator
