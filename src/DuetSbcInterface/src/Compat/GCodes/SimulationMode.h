/*
 * SimulationMode.h - compatibility shim
 *
 * Copied from RepRapFirmware. DDARing::Spin and DDA::Prepare branch on this to time a print without
 * moving anything; the values must keep their order, because both compare with >=.
 */

#ifndef SRC_COMPAT_GCODES_SIMULATIONMODE_H_
#define SRC_COMPAT_GCODES_SIMULATIONMODE_H_

#include <RepRapFirmware.h>

enum class SimulationMode : uint8_t
{
	off = 0,			// not simulating
	debug,				// simulating step generation
	normal,				// not generating steps, just timing
	partial,			// simulating step generation
	highest = partial
};

#endif /* SRC_COMPAT_GCODES_SIMULATIONMODE_H_ */
