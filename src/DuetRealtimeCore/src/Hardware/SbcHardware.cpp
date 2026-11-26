#include "SbcHardware.h"

#include <Hardware/Emulator/EmulatorLink.h>
#include <Hardware/GpioInputPin.h>
#include <Hardware/OutputGpioPin.h>
#include <Hardware/SpiDevice.h>

#include <stdexcept>
#include <string>

namespace Duet::Sbc
{
	namespace
	{
		SbcHardware CreateLinuxHardware(const Config& config)
		{
			SbcHardware hardware;
			// The interface thread waits on these lines' fds directly with poll(); there is no separate
			// monitor thread, so a pin edge wakes the interface thread in a single hop.
			hardware.dataAvailable = std::make_unique<GpioInputPin>(
				config.gpioChipDevice, config.dataAvailablePin, "sbc-dap-" + std::to_string(config.dataAvailablePin));
			hardware.transferReady = std::make_unique<GpioInputPin>(
				config.gpioChipDevice, config.transferReadyPin, "sbc-trp-" + std::to_string(config.transferReadyPin));
			if (config.sbcDataAvailablePin >= 0)
			{
				hardware.sbcDataAvailable =
					std::make_unique<OutputGpioPin>(config.gpioChipDevice,
													config.sbcDataAvailablePin,
													"sbc-sbcdap-" + std::to_string(config.sbcDataAvailablePin),
													false);
			}
			hardware.spi = std::make_unique<SpiDevice>(config.spiDevice, config.spiFrequency, config.spiTransferMode);
			return hardware;
		}

		SbcHardware CreateEmulatedHardware(const Config& config)
		{
			// The scope-trigger output has nothing to drive in an emulator, so it is left out
			auto link = std::make_shared<Emulator::EmulatorLink>(
				config.emulatorSocketPath, config.sbcConnectTimeout, config.sbcTransferTimeout);
			Emulator::EmulatorLink::SetCurrent(link);
			SbcHardware hardware;
			hardware.spi = std::make_unique<Emulator::EmulatedSpiBus>(link);
			hardware.transferReady = std::make_unique<Emulator::EmulatedInputLine>(link, Emulator::Line::TransferReady);
			hardware.dataAvailable = std::make_unique<Emulator::EmulatedInputLine>(link, Emulator::Line::DataAvailable);
			return hardware;
		}
	} // namespace

	SbcHardware CreateSbcHardware(const Config& config)
	{
		switch (config.spiHardware)
		{
		case SpiHardwareKind::Linux:
			return CreateLinuxHardware(config);
		case SpiHardwareKind::Emulator:
			return CreateEmulatedHardware(config);
		}
		throw std::invalid_argument("no SPI hardware for the configured kind");
	}

} // namespace Duet::Sbc
