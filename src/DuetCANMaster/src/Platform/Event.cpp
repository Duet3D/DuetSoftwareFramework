/*
 * Event.cpp
 *
 *  Created on: 18 Oct 2021
 *      Author: David
 */

#include <Platform/Platform.h>

#include <Platform/Event.h>
#include <RepRapFirmware.h>

Event* _ecv_null Event::eventsPending = nullptr;
unsigned int Event::eventsQueued = 0;
unsigned int Event::eventsProcessed = 0;

// Private constructor, inline because it is only called from one place
inline Event::Event(Event* _ecv_null pNext,
					const EventType& et,
					uint16_t pParam,
					CanAddress pBa,
					uint8_t devNum,
					const char* _ecv_array format,
					va_list vargs) noexcept
	: m_next(pNext)
	, m_param(pParam)
	, m_type(et)
	, m_boardAddress(pBa)
	, m_deviceNumber(devNum)
	, m_isBeingProcessed(false)
{
	m_text.vprintf(format, vargs);
}

// Queue an event, or release it if we have a similar event pending already. Returns true if the event was added, false
// if it was released.
/*static*/ bool Event::AddEvent(
	const EventType& et, uint16_t pParam, CanAddress pBa, uint8_t devNum, const char* _ecv_array format, ...) noexcept
{
	va_list vargs;
	va_start(vargs, format);
	const bool ret = AddEventV(et, pParam, pBa, devNum, format, vargs);
	va_end(vargs);
	return ret;
}

// Queue an event unless we have a similar event pending already. Returns true if the event was added.
// The event list is held in priority order, lowest numbered (highest priority) events first.
/*static*/ bool Event::AddEventV(const EventType& et,
								 uint16_t pParam,
								 CanAddress pBa,
								 uint8_t devNum,
								 const char* _ecv_array format,
								 va_list vargs) noexcept
{
	// Search for similar events already pending or being processed.
	// An event is 'similar' if it has the same type, device number, CAN address and parameter even if the text is
	// different.
	const TaskCriticalSectionLocker lock;

	Event* _ecv_null* pe = &eventsPending;
	while (*pe != nullptr &&
		   (et >= (*pe)->m_type ||
			(*pe)->m_isBeingProcessed)) // while the next event in the list has same or higher priority than the new one
	{
		if (et == (*pe)->m_type && devNum == (*pe)->m_deviceNumber && (*pe)->m_param == pParam
#if SUPPORT_CAN_EXPANSION
			&& pBa == (*pe)->m_boardAddress
#endif
		)
		{
			return false; // there is a similar event already in the queue
		}
		pe = &((*pe)->m_next);
	}

	// We didn't find a similar event, so add the new one
	*pe = new Event(*pe, et, pParam, pBa, devNum, format, vargs);
	++eventsQueued;
	return true;
}

#if SUPPORT_CAN_EXPANSION

// Queue an event received via CAN
/*static*/ void Event::Add(const CanMessageEvent& msg, CanAddress src, size_t msgLen) noexcept
{
	// We need to make sure that the text is null terminated
	String<StringLength100> msgText;
	msgText.copy(msg.text, msg.GetMaxTextLength(msgLen));
	(void)AddEvent((EventType)msg.eventType, msg.eventParam, src, msg.deviceNumber, "%s", msgText.c_str());
}

#endif

// Get the highest priority event and mark it as being serviced
/*static*/ bool Event::StartProcessing() noexcept
{
	const TaskCriticalSectionLocker lock;

	Event* const _ecv_null ev = eventsPending;
	if (ev == nullptr)
	{
		return false;
	}
	ev->m_isBeingProcessed = true;
	return true;
}

// Get the name of the macro that we run when this event occurs
/*static*/ void Event::GetMacroFileName(const StringRef& fname) noexcept
{
	const Event* const _ecv_null ep = eventsPending;
	if (ep != nullptr && ep->m_isBeingProcessed)
	{
		fname.copy(ep->m_type.ToString());
		fname.ReplaceAll('_', '-');
		fname.cat(".g");
	}
}

// Get the default action for the current event
/*static*/ PrintPausedReason Event::GetDefaultPauseReason() noexcept
{
	const Event* const _ecv_null ep = eventsPending;
	if (ep != nullptr && ep->m_isBeingProcessed)
	{
		switch (ep->m_type.RawValue())
		{
		case EventType::heater_fault:
			return PrintPausedReason::HeaterFault;

		case EventType::filament_error:
			return PrintPausedReason::FilamentError;

		case EventType::driver_error:
			return PrintPausedReason::DriverError;

		default:
			break;
		}
	}
	return PrintPausedReason::DontPause;
}

// Mark the highest priority event as completed
/*static*/ void Event::FinishedProcessing() noexcept
{
	const TaskCriticalSectionLocker lock;

	const Event* _ecv_null ev = eventsPending;
	if (ev != nullptr && ev->m_isBeingProcessed)
	{
		eventsPending = ev->m_next;
		delete ev;
		++eventsProcessed;
	}
}

// Get a description of the current event
/*static*/ MessageType Event::GetTextDescription(const StringRef& str) noexcept
{
	const Event* const _ecv_null ep = eventsPending;
	if (ep != nullptr && ep->m_isBeingProcessed)
	{
		switch (ep->m_type.RawValue())
		{
		case EventType::heater_fault:
		{
			const char* _ecv_array heaterFaultText =
				HeaterFaultText[min<size_t>(ep->m_param, ARRAY_SIZE(HeaterFaultText) - 1)];
			str.printf("Heater %u fault: %s%s", ep->m_deviceNumber, heaterFaultText, ep->m_text.c_str());
		}
			return ErrorMessage;

		case EventType::filament_error:
			str.printf(
				"Filament error on extruder %u: %s", ep->m_deviceNumber, FilamentSensorStatus(ep->m_param).ToString());
			return ErrorMessage;

		case EventType::driver_error:
#if SUPPORT_CAN_EXPANSION
			str.printf("Driver %u.%u error: ", ep->m_boardAddress, ep->m_deviceNumber);
#else
			str.printf("Driver %u error: ", ep->deviceNumber);
#endif
			StandardDriverStatus(ep->m_param).AppendText(str, 2);
			str.cat(ep->m_text.c_str());
			return ErrorMessage;

		case EventType::driver_warning:
#if SUPPORT_CAN_EXPANSION
			str.printf("Driver %u.%u warning: ", ep->m_boardAddress, ep->m_deviceNumber);
#else
			str.printf("Driver %u warning: ", ep->deviceNumber);
#endif
			StandardDriverStatus(ep->m_param).AppendText(str, 1);
			str.cat(ep->m_text.c_str());
			return WarningMessage;

		case EventType::driver_stall:
#if SUPPORT_CAN_EXPANSION
			str.printf("Driver %u.%u stall", ep->m_boardAddress, ep->m_deviceNumber);
#else
			str.printf("Driver %u stall", ep->deviceNumber);
#endif
			return WarningMessage;

		case EventType::main_board_power_fail:
			// This does not currently generate an event, so no text
			return ErrorMessage;

		case EventType::mcu_temperature_warning:
#if SUPPORT_CAN_EXPANSION
			str.printf("MCU temperature warning from board %u: temperature %.1fC",
					   ep->m_boardAddress,
					   (double)((float)ep->m_param / 10));
#else
			str.printf("MCU temperature warning: temperature %.1fC", (double)((float)ep->param / 10.0));
#endif
			return WarningMessage;

		case EventType::overvoltage:
#if SUPPORT_CAN_EXPANSION
			str.printf("overvoltage on board %u: voltage %.1fV", ep->m_boardAddress, (double)((float)ep->m_param / 10));
#else
			str.printf("overvoltage: voltage %.1fV", (double)((float)ep->param / 10.0));
#endif
			return WarningMessage;

		case EventType::undervoltage:
#if SUPPORT_CAN_EXPANSION
			str.printf(
				"undervoltage on board %u: voltage %.1fV", ep->m_boardAddress, (double)((float)ep->m_param / 10));
#else
			str.printf("undervoltage: voltage %.1fV", (double)((float)ep->param / 10.0));
#endif
			return WarningMessage;

		case EventType::expansion_timeout:
			str.printf("Expansion board %u stopped sending status", ep->m_boardAddress);
			return ErrorMessage;

		case EventType::expansion_reconnect:
			str.printf("Expansion board %u reconnected", ep->m_boardAddress);
			return ErrorMessage;
		}
	}
	str.copy("Internal error in Event");
	return ErrorMessage;
}

// Generate diagnostic data
/*static*/ void Event::Diagnostics(const StringRef& reply, Platform& /*p*/) noexcept
{
	reply.lcatf("Events: %u queued, %u completed", eventsQueued, eventsProcessed);
}

// End
