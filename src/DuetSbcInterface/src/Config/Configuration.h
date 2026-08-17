// Runtime configuration for the SBC interface. Mirrors the relevant fields of
// DuetControlServer/Settings.cs so the standalone harness and a future C# consumer share defaults.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace Duet::Sbc
{

	// Which transport carries the link. Only Spi exists; the enum is here so that the choice has a
	// place to live and CreateTransport has something to switch on. See Interface/Transport.h.
	enum class TransportKind : uint8_t
	{
		Spi = 0
	};

	struct Config
	{
		TransportKind transport = TransportKind::Spi;

		// SPI device
		std::string spiDevice = "/dev/spidev0.0";
		uint32_t spiFrequency = 8'000'000;
		int spiTransferMode = 0;
		size_t bufferSize = 8192;

		// GPIO
		std::string gpioChipDevice = "/dev/gpiochip0";
		int transferReadyPin = 25; // TfrRdy input from the Duet
		int dataAvailablePin = 24; // DataAvailable input from the Duet
		// Optional output line driven high while the SBC has data staged for transfer and low once the
		// transfer completes (a scope trigger for the request->transfer latency). < 0 disables it.
		int sbcDataAvailablePin = -1;

		// Thread placement (see ProcessHelpers)
		bool isolateInterfaceThread = true;
		int isolatedCoreId = 3;
		bool useRealtimeScheduling = true;
		// Real-time priority for the interface thread, which blocks in poll() on the GPIO edge fd and must
		// wake promptly on the TfrRdy edge. There is no separate GPIO monitor thread.
		int interfaceRtPriority = 50;

		// Timeouts (milliseconds), matching Settings.cs
		int sbcConnectTimeout = 500;
		int sbcTransferTimeout = 500;
		int sbcConnectionTimeout = 4000;
		int sbcConnectionKeepAliveInterval = 25;
		int maxSbcRetries = 3;

		// When set, DCS is running in update-only mode: a firmware reporting a newer protocol version
		// than this build understands is accepted rather than rejected, so it can still be flashed
		// (Settings.UpdateOnly, see ExchangeHeader).
		bool updateOnly = false;
	};

} // namespace Duet::Sbc
