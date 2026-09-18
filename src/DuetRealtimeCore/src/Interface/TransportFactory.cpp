/*
 * TransportFactory.cpp - see TransportFactory.h.
 */

#include "TransportFactory.h"

#include <Interface/SPI/SpiTransfer.h>
#include <Interface/Socket/SocketTransport.h>

#include <stdexcept>

namespace Duet::Sbc
{
	std::unique_ptr<Transport> CreateTransport(const Config& config)
	{
		switch (config.transport)
		{
		case TransportKind::Spi:
			return std::make_unique<SpiTransfer>(config);
		case TransportKind::Socket:
			return std::make_unique<SocketTransport>(config);
		}
		throw std::invalid_argument("no transport for the configured kind");
	}
}
