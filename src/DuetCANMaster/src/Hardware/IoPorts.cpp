/*
 * IoPort.cpp
 *
 *  Created on: 30 Sep 2017
 *      Author: David
 */

#include "IoPorts.h"
#include <Platform/RepRap.h>

#include <Platform/Platform.h>

#ifdef DUET_NG
#  include <DuetNG/DueXn.h>
#endif

#include <AnalogOut.h>
#include <Interrupts.h>

#include <AnalogIn.h>

#if SAME5x
constexpr unsigned int AdcBits = AnalogIn::AdcBits;
#else
constexpr unsigned int adcBits = LegacyAnalogIn::AdcBits;
#endif

#if SUPPORT_CAN_EXPANSION
#  include <CanId.h>
#endif

#if SUPPORT_REMOTE_COMMANDS
#  include <CAN/CanInterface.h>
#endif

// Try to assign ports, returning the number of ports successfully assigned
/*static*/ size_t IoPort::AssignPorts(const char* _ecv_array pinNames,
									  const StringRef& reply,
									  PinUsedBy neededFor,
									  size_t numPorts,
									  IoPort* const _ecv_from ports[],
									  const PinAccess access[]) noexcept
{
	// Release any existing assignments
	for (size_t i = 0; i < numPorts; ++i)
	{
		ports[i]->Release();
	}

	// Parse the string into individual port names
	size_t index = 0;
	for (size_t i = 0; i < numPorts; ++i)
	{
		// Get the next port name
		String<StringLength50> pn;
		char c = 0;
		while ((c = pinNames[index]) != 0 && c != '+')
		{
			pn.cat(c);
			++index;
		}

#if SUPPORT_CAN_EXPANSION
		const CanAddress boardAddress = RemoveBoardAddress(pn.GetRef());
		if (boardAddress != CanInterface::GetCanAddress())
		{
			reply.lcat("Port must be on main board");
#else
		if (!RemoveBoardAddress(pn.GetRef()))
		{
			reply.lcat("Board address of port must be 0");
#endif
			for (size_t j = 0; j < i; ++j)
			{
				ports[j]->Release();
			}
			return 0;
		}

		// Try to allocate the port
		if (!ports[i]->Allocate(pn.c_str(), reply, neededFor, access[i]))
		{
			for (size_t j = 0; j < i; ++j)
			{
				ports[j]->Release();
			}
			return 0;
		}

		if (c != '+')
		{
			return i + 1;
		}
		++index; // skip the "+"
	}
	return numPorts;
}

bool IoPort::AssignPort(const char* _ecv_array pinName,
						const StringRef& reply,
						PinUsedBy neededFor,
						PinAccess access) noexcept
{
	IoPort* const _ecv_from p = this;
	return AssignPorts(pinName, reply, neededFor, 1, (&p), &access) == 1;
}

/*static*/ const char* _ecv_array IoPort::TranslatePinAccess(PinAccess access) noexcept
{
	switch (access)
	{
	case PinAccess::Read:
		return "digital read";
	case PinAccess::ReadWithPullupInternalUseOnly:
		return "digital read (pullup resistor enabled)";
	case PinAccess::ReadNoDebounce:
		return "digital read (no debouncing)";
	case PinAccess::ReadAnalog:
		return "analog read";
	case PinAccess::Write0:
		return "write (initially low)";
	case PinAccess::Write1:
		return "write (initially high)";
	case PinAccess::pwm:
		return "write PWM";
	case PinAccess::Servo:
		return "servo write";
	default:
		return "[unknown]";
	}
}

// Members of class IoPort

PinUsedBy IoPort::portUsedBy[NumNamedPins];
int8_t IoPort::logicalPinModes[NumNamedPins]; // what mode each logical pin is set to - would ideally be class PinMode
											  // not int8_t

/*static*/ void IoPort::Init() noexcept
{
	for (PinUsedBy& p : portUsedBy)
	{
		p = PinUsedBy::NotUsed;
	}
	for (int8_t& p : logicalPinModes)
	{
		p = PIN_MODE_NOT_CONFIGURED;
	}
}

IoPort::IoPort() noexcept
	: logicalPin(NoLogicalPin)
	, hardwareInvert(false)
	, totalInvert(false)
	, isSharedInput(false)
{
}

void IoPort::Release() noexcept
{
	if (IsValid() && !isSharedInput)
	{
		DetachInterrupt();
#if SAME5x
		ClearAnalogCallback();
#endif
		portUsedBy[logicalPin] = PinUsedBy::NotUsed;
		logicalPinModes[logicalPin] = PIN_MODE_NOT_CONFIGURED;
	}
	logicalPin = NoLogicalPin;
	hardwareInvert = totalInvert = false;
}

// Attach an interrupt to the pin. Not permitted if we allocated the pin in shared input mode.
bool IoPort::AttachInterrupt(StandardCallbackFunction callback,
							 InterruptMode mode,
							 CallbackParameter param) const noexcept
{
	return IsValid() && !isSharedInput && AttachPinInterrupt(GetPinNoCheck(), callback, mode, param);
}

void IoPort::DetachInterrupt() const noexcept
{
	if (IsValid() && !isSharedInput)
	{
		DetachPinInterrupt(logicalPin);
	}
}

#if SAME5x

bool IoPort::SetAnalogCallback(AnalogInCallbackFunction fn, CallbackParameter cbp, uint32_t ticksPerCall) noexcept
{
	return IsValid() && !isSharedInput && AnalogIn::SetCallback(GetAnalogChannel(), fn, cbp, ticksPerCall);
}

void IoPort::ClearAnalogCallback() noexcept
{
	(void)SetAnalogCallback(nullptr, CallbackParameter(), 1);
}

#endif

// Allocate the specified logical pin, returning true if successful
bool IoPort::Allocate(const char* _ecv_array pn, const StringRef& reply, PinUsedBy neededFor, PinAccess access) noexcept
{
	Release();

	bool inverted = false;
	for (;;)
	{
		if (*pn == '!')
		{
			inverted = !inverted;
		}
		else if (*pn == '^')
		{
			// Note, enabling the pullup for external ports on Duet 3 boards is not needed because there is an external
			// pullup, and a generally a bad idea because there is a series protection resistor, so the noise margin
			// will be reduced if the internal pullup is enabled. There are a few pins for which this doesn't apply,
			// e.g. the CS pins on the SPI connector. Ideally we would include this info in the pin table, e.g. in the
			// bitmap of allowed pin modes.
			if (access == PinAccess::Read)
			{
				access = PinAccess::ReadWithPullupInternalUseOnly;
			}
		}
		else if (*pn == '*')
		{
			alternateConfig = true;
		}
		else
		{
			break;
		}
		++pn;
	}

	const char* const _ecv_array fullPinName = pn; // the full pin name less the inversion and pullup flags

#if SUPPORT_CAN_EXPANSION
	if (isDigit(*pn))
	{
		const uint32_t expansionNumber = StrToU32(pn, &pn);
		if (*pn != '.')
		{
			reply.printf("Bad pin name '%s'", fullPinName);
			return false;
		}
		if (expansionNumber != 0)
		{
			reply.printf("Pin '%s': only main board pins allowed here", fullPinName);
			return false;
		}
	}
#endif

	LogicalPin lp = 0;
	bool hwInvert = false;
	if (!LookupPinName(pn, lp, hwInvert))
	{
		reply.printf("Unknown pin name '%s'", fullPinName);
		return false;
	}

	if (lp != NoLogicalPin) // if not assigning "nil"
	{
		bool doSetMode = true;
		if (portUsedBy[lp] == PinUsedBy::NotUsed ||
			(portUsedBy[lp] == PinUsedBy::TemporaryInput && neededFor != PinUsedBy::TemporaryInput))
		{
			portUsedBy[lp] = neededFor;
		}
		else
		{
			const auto pm = (PinMode)logicalPinModes[lp];
			if (neededFor != PinUsedBy::TemporaryInput || (pm != INPUT && pm != INPUT_PULLUP))
			{
				reply.printf("Pin '%s' is not free", fullPinName);
				return false;
			}
			doSetMode = false;
		}
		logicalPin = lp;
		hardwareInvert = hwInvert;
		isSharedInput = (neededFor == PinUsedBy::TemporaryInput);
		SetInvert(inverted);

		if (doSetMode && !SetMode(access))
		{
			reply.printf("Pin '%s' does not support mode %s", fullPinName, TranslatePinAccess(access));
			Release();
			return false;
		}
	}

	return true;
}

// Set the specified pin mode returning true if successful
bool IoPort::SetMode(PinAccess access) noexcept
{
	if (!IsValid())
	{
		return false;
	}

	// Check that the pin mode has been defined suitably
	PinMode desiredMode{};
	switch (access)
	{
	case PinAccess::Write0:
		desiredMode = (totalInvert) ? OUTPUT_HIGH : OUTPUT_LOW;
		break;
	case PinAccess::Write1:
		desiredMode = (totalInvert) ? OUTPUT_LOW : OUTPUT_HIGH;
		break;
	case PinAccess::pwm:
	case PinAccess::Servo:
		desiredMode = (totalInvert) ? OUTPUT_PWM_HIGH : OUTPUT_PWM_LOW;
		break;
	case PinAccess::ReadAnalog:
		desiredMode = AIN;
		break;
	case PinAccess::ReadWithPullupInternalUseOnly:
		desiredMode = INPUT_PULLUP;
		break;
	case PinAccess::Read:
	case PinAccess::ReadNoDebounce:
	default:
		desiredMode = INPUT;
		break;
	}

	if (logicalPinModes[logicalPin] != (int8_t)desiredMode)
	{
		const AnalogChannelNumber chan = PinToAdcChannel(GetPinNoCheck());
		if (chan != NO_ADC)
		{
			if (access == PinAccess::ReadAnalog)
			{
				IoPort::SetPinMode(
					GetPinNoCheck(),
					AIN); // SAME70 errata says we must disable the pullup resistor before enabling the AFEC channel
				AnalogInEnableChannel(chan, true);
				logicalPinModes[logicalPin] = (int8_t)desiredMode;
				return true;
			}
			else
			{
				AnalogInEnableChannel(chan, false);
			}
		}
		else if (access == PinAccess::ReadAnalog)
		{
			return false;
		}
		IoPort::SetPinMode(
			GetPinNoCheck(),
			desiredMode,
			access == PinAccess::Read); // debounce pins with external inputs, don't debounce pins used internally
		logicalPinModes[logicalPin] = (int8_t)desiredMode;
	}
	return true;
}

bool IoPort::GetInvert() const noexcept
{
	return (hardwareInvert) ? !totalInvert : (bool)totalInvert;
}

void IoPort::SetInvert(bool pInvert) noexcept
{
	totalInvert = (hardwareInvert) ? !pInvert : pInvert;
}

void IoPort::ToggleInvert(bool pInvert) noexcept
{
	if (pInvert)
	{
		totalInvert = !totalInvert;
	}
}

void IoPort::AppendBasicDetails(const StringRef& str) const noexcept
{
	if (IsValid())
	{
		str.catf(" pin ");
		AppendPinName(str);
		if (logicalPinModes[logicalPin] == INPUT_PULLUP)
		{
			str.cat(", pullup enabled");
		}
		else if (logicalPinModes[logicalPin] == INPUT)
		{
			str.cat(", pullup disabled");
		}
	}
	else
	{
		str.cat(" has no pin");
	}
}

// Append the names of the pin to a string, picking only those that have the correct hardware invert status
void IoPort::AppendPinName(const StringRef& str) const noexcept
{
	if (IsValid())
	{
		const char* _ecv_array _ecv_null pn = PinTable[logicalPin].GetNames();
		if (pn != nullptr)
		{
			if (GetInvert())
			{
				str.cat('!');
			}
			const size_t insertPoint = str.strlen();
			unsigned int numPrinted = 0;
			do
			{
				bool inverted = (*pn == '!');
				if (inverted)
				{
					++pn;
				}
				if (hardwareInvert)
				{
					inverted = !inverted;
				}
				if (inverted)
				{
					// skip this one
					while (*pn != 0 && *pn != ',')
					{
						++pn;
					}
				}
				else
				{
					// Include this one
					if (numPrinted != 0)
					{
						str.cat(',');
					}
					++numPrinted;
					while (*pn != 0 && *pn != ',')
					{
						str.cat(*pn);
						++pn;
					}
				}

			} while (*pn++ == ',');

			if (numPrinted > 1)
			{
				str.Insert(insertPoint, '(');
				str.cat(')');
			}
		}
		return;
	}

	str.cat(NoPinName);
}

/*static*/ void IoPort::AppendPinNames(const StringRef& str, size_t numPorts, const IoPort* const ports[]) noexcept
{
	for (size_t i = 0; i < numPorts; ++i)
	{
		if (ports[i]->IsValid())
		{
			if (i != 0)
			{
				str.cat('+');
			}
			ports[i]->AppendPinName(str);
		}
		else
		{
			if (i == 0)
			{
				str.cat(NoPinName);
			}
			break;
		}
	}
}

void IoPort::WriteDigital(bool high) const noexcept
{
	if (IsValid())
	{
		WriteDigital(GetPinNoCheck(), (totalInvert) ? !high : high);
	}
}

Pin IoPort::GetPin() const noexcept
{
	return (IsValid()) ? GetPinNoCheck() : NoPin;
}

// Get the capabilities of the pin
PinCapability IoPort::GetCapability() const noexcept
{
	return (IsValid()) ? PinTable[GetPinNoCheck()].cap : PinCapability::None;
}

bool IoPort::ReadDigital() const noexcept
{
	if (IsValid())
	{
		const bool b = ReadPin(GetPinNoCheck());
		return (totalInvert) ? !b : b;
	}
	return false;
}

uint16_t IoPort::ReadAnalog() const noexcept
{
	const uint16_t val = AnalogInReadChannel(GetAnalogChannel());
	return (totalInvert) ? ((1u << adcBits) - 1) - val : val;
}

#if SUPPORT_CAN_EXPANSION
// Remove the board address from a port name string and return it
/*static*/ CanAddress IoPort::RemoveBoardAddress(const StringRef& portName) noexcept
#else
// Remove the board address if present, returning true if it was zero or not present
/*static*/ bool IoPort::RemoveBoardAddress(const StringRef& portName) noexcept
#endif
{
	size_t prefix = 0;
	while (portName[prefix] == '!' || portName[prefix] == '^' || portName[prefix] == '*')
	{
		++prefix;
	}

	size_t numToSkip = prefix;
	unsigned int boardAddress = 0;
	while (isDigit(portName[numToSkip]))
	{
		boardAddress = (boardAddress * 10) + (unsigned int)(portName[numToSkip] - '0');
		++numToSkip;
	}
#if SUPPORT_CAN_EXPANSION
	if (numToSkip != prefix && portName[numToSkip] == '.' && boardAddress <= CanId::MaxCanAddress)
	{
		portName.Erase(prefix, numToSkip - prefix + 1); // remove the board address prefix
		return (CanAddress)boardAddress;
	}
	return CanInterface::GetCanAddress();
#else
	if (numToSkip != prefix && portName[numToSkip] == '.')
	{
		if (boardAddress != 0)
		{
			return false;
		}
		portName.Erase(prefix, numToSkip - prefix + 1); // remove the board address prefix
	}
	return true;
#endif
}

// Low level pin access methods

#ifdef DUET_NG

/*static*/ void IoPort::SetPinMode(Pin pin, PinMode mode, bool debounce) noexcept
{
	if (pin >= DueXnExpansionStart)
	{
		// Note: the SX1509B I/O expander chip doesn't seem to work if you set PWM mode and then set digital output
		// mode.
		DuetExpansion::SetPinMode(pin, mode, debounce);
	}
	else
	{
		::SetPinMode(pin, mode, debounce);
	}
}

/*static*/ bool IoPort::ReadPin(Pin pin) noexcept
{
	if (pin >= DueXnExpansionStart)
	{
		return DuetExpansion::DigitalRead(pin);
	}
	else
	{
		return digitalRead(pin);
	}
}

/*static*/ void IoPort::WriteDigital(Pin pin, bool high) noexcept
{
	if (pin >= DueXnExpansionStart)
	{
		DuetExpansion::DigitalWrite(pin, high);
	}
	else
	{
		digitalWrite(pin, high);
	}
}

/*static*/ void IoPort::WriteAnalog(Pin pin, float pwm, uint16_t freq) noexcept
{
	if (pin >= DueXnExpansionStart)
	{
		DuetExpansion::AnalogOut(pin, pwm);
	}
	else
	{
		AnalogOut::Write(pin, pwm, freq);
	}
}

#endif // ifdef DUET_NG

// Members of class PwmPort
PwmPort::PwmPort() noexcept
	: m_frequency(DefaultPinWritePwmFreq)
{
}

// Append the frequency if the port is valid
void PwmPort::AppendFrequency(const StringRef& str) const noexcept
{
	if (IsValid())
	{
		str.catf(" frequency %uHz", m_frequency);
	}
}

void PwmPort::AppendFullDetails(const StringRef& str) const noexcept
{
	AppendBasicDetails(str);
	AppendFrequency(str);
}

void PwmPort::WriteAnalog(float pwm) const noexcept
{
	if (IsValid())
	{
		IoPort::WriteAnalog(GetPinNoCheck(), ((totalInvert) ? 1.0f - pwm : pwm), m_frequency);
	}
}

bool PwmPort::SupportsPwm() const noexcept
{
	return IsValid() && (((uint8_t)PinTable[logicalPin].GetCapability() & (uint8_t)PinCapability::pwm) != 0);
}

// End
