// The pins and SPI bus that connect the SBC to the controller, as SpiTransfer uses them.
//
// SpiTransfer is written against these interfaces rather than the Linux devices, so that the same
// transfer code runs against a real controller (spidev and the GPIO character device) and against an
// emulated one (the emulator link, Hardware/Emulator). CreateSbcHardware picks the implementation the
// configuration names.
#pragma once

#include <Config/Configuration.h>

#include <cstddef>
#include <cstdint>
#include <memory>

namespace Duet::Sbc
{

	// A full-duplex SPI bus on which the SBC is the master
	class SpiBus
	{
	  public:
		SpiBus() = default;
		virtual ~SpiBus() = default;

		SpiBus(const SpiBus&) = delete;
		SpiBus& operator=(const SpiBus&) = delete;
		SpiBus(SpiBus&&) = delete;
		SpiBus& operator=(SpiBus&&) = delete;

		// Clock `length` bytes out of `tx` while clocking the same number into `rx`, with the chip
		// select asserted for the whole transfer. `tx` and `rx` may be the same buffer.
		virtual void TransferFullDuplex(const uint8_t* tx, const uint8_t* rx, size_t length) const = 0;
	};

	// An input line from the controller whose edges can be waited for with poll()
	class InputLine
	{
	  public:
		InputLine() = default;
		virtual ~InputLine() = default;

		InputLine(const InputLine&) = delete;
		InputLine& operator=(const InputLine&) = delete;
		InputLine(InputLine&&) = delete;
		InputLine& operator=(InputLine&&) = delete;

		// File descriptor that polls readable (POLLIN) while an edge event is queued
		[[nodiscard]] virtual int Fd() const noexcept = 0;

		// The level of the line now
		[[nodiscard]] virtual bool Read() const = 0;

		// Consume one queued edge event without blocking. Returns false if none was queued.
		virtual bool ReadEvent() = 0;

		// Sequence number of the last rising edge consumed by ReadEvent. Edges are numbered in the
		// order they happened, so a newer rising edge has a larger number.
		[[nodiscard]] virtual uint32_t RisingSequenceNumber() const noexcept = 0;

		// Edges that were lost before ReadEvent could consume them
		[[nodiscard]] virtual int MissedEdges() const noexcept = 0;
	};

	// An output line from the SBC
	class OutputLine
	{
	  public:
		OutputLine() = default;
		virtual ~OutputLine() = default;

		OutputLine(const OutputLine&) = delete;
		OutputLine& operator=(const OutputLine&) = delete;
		OutputLine(OutputLine&&) = delete;
		OutputLine& operator=(OutputLine&&) = delete;

		virtual void Write(bool value) = 0;
	};

	// Everything SpiTransfer drives. sbcDataAvailable is null when the configuration has no such line.
	struct SbcHardware
	{
		std::unique_ptr<SpiBus> spi;
		std::unique_ptr<InputLine> transferReady;
		std::unique_ptr<InputLine> dataAvailable;
		std::unique_ptr<OutputLine> sbcDataAvailable;
	};

	// Open the bus and lines the configuration names. Throws if they cannot be opened.
	SbcHardware CreateSbcHardware(const Config& config);

} // namespace Duet::Sbc
