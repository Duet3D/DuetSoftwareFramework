/*
 * PinDescription.h
 *
 *  Created on: 10 Jul 2020
 *      Author: David
 */

#ifndef SRC_HARDWARE_SAME70_PINDESCRIPTION_H_
#define SRC_HARDWARE_SAME70_PINDESCRIPTION_H_

#include <CoreIO.h>

// Enum to represent allowed types of pin access
// We don't have a separate bit for servo, because Duet PWM-capable ports can be used for servos if they are on the Duet
// main board
enum class PinCapability : uint8_t
{
	// Individual capabilities
	None = 0u,
	Read = 1u,	 // digital read
	Ain = 2u,	 // analog read
	Write = 4u,	 // digital write
	pwm = 8u,	 // PWM write
	NpDma = 16u, // Neopixel output using DMA e.g. using SPI MOSI

	// Combinations
	Ainr = 1u | 2u,
	Rw = 1u | 4u,
	Wpwm = 4u | 8u,
	Rwpwm = 1u | 4u | 8u,
	Ainrw = 1u | 2u | 4u,
	Ainrwpwm = 1u | 2u | 4u | 8u,
	NpDmaW = 4u | 16u
};

constexpr PinCapability operator|(PinCapability a, PinCapability b) noexcept
{
	return (PinCapability)((uint8_t)a | (uint8_t)b);
}

constexpr PinCapability operator&(PinCapability a, PinCapability b) noexcept
{
	return (PinCapability)((uint8_t)a & (uint8_t)b);
}

// The pin description says what functions are available on each pin, filtered to avoid allocating the same function to
// more than one pin.. It is a struct not a class so that it can be direct initialised in read-only memory.
struct PinDescription : public PinDescriptionBase
{
	PinCapability cap;
	const char* _ecv_array null pinNames;

	[[nodiscard]] PinCapability GetCapability() const noexcept { return cap; }
	[[nodiscard]] const char* _ecv_array null GetNames() const noexcept { return pinNames; }
};

#endif /* SRC_HARDWARE_SAME70_PINDESCRIPTION_H_ */
