/*
 * TransportFactory.h
 *
 * Builds the transport the configuration asks for.
 *
 * This is the one place that names a concrete transport. LinkService takes what it is given and the
 * CApi passes the configuration through, so adding a second transport means writing an
 * implementation of Transport and adding a case here - not editing the loop.
 */

#ifndef SRC_INTERFACE_TRANSPORTFACTORY_H_
#define SRC_INTERFACE_TRANSPORTFACTORY_H_

#include <Config/Configuration.h>
#include <Interface/Transport.h>

#include <memory>

namespace Duet::Sbc
{
	// Never returns null: an unrecognised kind is a configuration this build cannot serve, and
	// failing at construction is better than a link that is silently not there.
	std::unique_ptr<Transport> CreateTransport(const Config& config);
}

#endif /* SRC_INTERFACE_TRANSPORTFACTORY_H_ */
