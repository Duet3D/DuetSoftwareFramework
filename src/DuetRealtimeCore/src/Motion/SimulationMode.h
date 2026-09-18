/*
 * SimulationMode.h
 *
 * DDARing::Spin and DDA::Prepare branch on this to time a print without moving anything.
 *
 * The order is load-bearing: both compare with >= and <, so the four values are a scale rather than
 * a set of labels. Only Off and Normal are reachable today - DuetControlServer does its own
 * simulation timing - but the two that are not cost a line each and removing them would leave a
 * scale with a gap in it.
 */

#ifndef SRC_MOTION_SIMULATIONMODE_H_
#define SRC_MOTION_SIMULATIONMODE_H_

#include <Config/MachineLimits.h>

enum class SimulationMode : uint8_t
{
	Off = 0,	 // not simulating
	Debug = 1,	 // not reachable: DuetControlServer owns simulation
	Normal = 2,	 // not generating steps, just timing
	Partial = 3, // not reachable: DuetControlServer owns simulation
	Highest = Partial
};

#endif /* SRC_MOTION_SIMULATIONMODE_H_ */
