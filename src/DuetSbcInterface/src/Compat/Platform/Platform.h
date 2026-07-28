/*
 * Platform.h - compatibility shim
 *
 * The imported motion sources use Platform only to emit messages. Everything else the firmware's
 * Platform does - pins, ADCs, power monitoring, the file system - has no counterpart on the SBC or
 * belongs to DuetControlServer.
 *
 * Messages go to the same sink as debugPrintf; see Compat/Diagnostics.h for why that is a callback
 * rather than a file descriptor.
 */

#ifndef SRC_COMPAT_PLATFORM_PLATFORM_H_
#define SRC_COMPAT_PLATFORM_PLATFORM_H_

#include <RepRapFirmware.h>

// The subset of the firmware's MessageType that reaches this code. The values do not have to match
// the firmware's: nothing on this side puts them on the wire, and DCS classifies messages itself
// from the LinkEvents log level.
enum MessageType : uint32_t
{
	DebugMessage = 0,
	GenericMessage,
	WarningMessage,
	ErrorMessage
};

class Platform
{
public:
	static void Message(MessageType type, const char *message) noexcept;
	static void MessageF(MessageType type, const char *fmt, ...) noexcept __attribute__((format(printf, 2, 3)));
};

#endif /* SRC_COMPAT_PLATFORM_PLATFORM_H_ */
