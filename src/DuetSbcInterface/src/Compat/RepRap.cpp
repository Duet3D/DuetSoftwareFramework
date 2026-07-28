/*
 * RepRap.cpp - the singletons behind the `reprap` facade. See Compat/Platform/RepRap.h.
 */

#include <Diagnostics.h>
#include <Motion/MotionSystem.h>
#include <Platform/Platform.h>
#include <Platform/RepRap.h>

#include <cstdio>

Duet::Sbc::Motion::MotionSystem RepRapShim::move;
Platform RepRapShim::platform;

RepRapShim reprap;

RepRapShim::RepRapShim() noexcept : gCodes(move)
{
}

bool RepRapShim::Debug(Module) noexcept
{
	return false;
}

AxesBitmap RepRapShim::GetDebugFlags(Module) noexcept
{
	return AxesBitmap();
}

size_t GCodesShim::GetTotalAxes() const noexcept
{
	return move.GetConfig().numTotalAxes;
}

size_t GCodesShim::GetVisibleAxes() const noexcept
{
	return move.GetConfig().numVisibleAxes;
}

size_t GCodesShim::GetNumExtruders() const noexcept
{
	return move.GetConfig().numExtruders;
}

void Platform::Message(MessageType, const char *message) noexcept
{
	debugPrintf("%s", message);
}

void Platform::MessageF(MessageType, const char *fmt, ...) noexcept
{
	// The message has to be formatted here rather than forwarded, because debugPrintf takes a
	// format string rather than a va_list.
	char buffer[256];
	va_list args;
	va_start(args, fmt);
	(void)vsnprintf(buffer, sizeof(buffer), fmt, args);
	va_end(args);
	debugPrintf("%s", buffer);
}
