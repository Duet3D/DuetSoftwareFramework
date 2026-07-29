/*
 * RepRap.cpp - the singletons behind the `reprap` facade. See Compat/Platform/RepRap.h.
 */

#include <Diagnostics.h>
#include <Motion/MotionSystem.h>
#include <Platform/Platform.h>
#include <Platform/RepRap.h>

#include <cstdio>

RepRapShim reprap;

RepRapShim::RepRapShim() noexcept : m_gCodes(m_move)
{
}

bool RepRapShim::Debug(Module /*unused*/) noexcept
{
	return false;
}

AxesBitmap RepRapShim::GetDebugFlags(Module /*unused*/) noexcept
{
	return AxesBitmap();
}

size_t GCodesShim::GetTotalAxes() const noexcept
{
	return m_move->GetConfig().numTotalAxes;
}

size_t GCodesShim::GetVisibleAxes() const noexcept
{
	return m_move->GetConfig().numVisibleAxes;
}

size_t GCodesShim::GetNumExtruders() const noexcept
{
	return m_move->GetConfig().numExtruders;
}

void Platform::Message(MessageType /*unused*/, const char *message) noexcept
{
	DebugPrintf("%s", message);
}

void Platform::MessageF(MessageType /*unused*/, const char *fmt, ...) noexcept
{
	// The message has to be formatted here rather than forwarded, because debugPrintf takes a
	// format string rather than a va_list.
	char buffer[256];
	va_list args;
	va_start(args, fmt);
	(void)vsnprintf(buffer, sizeof(buffer), fmt, args);
	va_end(args);
	DebugPrintf("%s", buffer);
}
