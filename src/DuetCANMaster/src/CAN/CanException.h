/*
 * ParseException.h
 *
 *  Created on: 21 Dec 2019
 *      Author: David
 */

#ifndef SRC_GCODES_CANEXCEPTION_H_
#define SRC_GCODES_CANEXCEPTION_H_

#include <RepRapFirmware.h>

namespace StackUsage
{
	constexpr uint32_t Throw = 1050; // how much stack we need to throw an exception
	constexpr uint32_t Margin = 300; // the margin we allow for calls to non-recursive functions that can throw
} // namespace StackUsage

// This message is used in many places. Define it here to ensure consistency.
constexpr const char* _ecv_array ArrayIndexOutOfRangeText = "array index out of bounds";

// This class is mostly used to throw exceptions when processing GCode. It is also used to store error messages that
// need to be retrieved later. Field "message" should always point to a constant string in flash memory, or be null. The
// error message may have a string, int32_t or uint32_t parameter
class CanException
{
  public:
	CanException() noexcept
		: message(nullptr)
	{
	}
	explicit CanException(const char* _ecv_array msg) noexcept
		: message(msg)
	{
		param.i = 0;
	}

	CanException(const char* _ecv_array msg, const char* _ecv_array sparam) noexcept
		: message(msg)
	{
		stringParam.copy(sparam);
	}

	CanException(const char* _ecv_array msg, uint32_t uparam) noexcept
		: message(msg)
	{
		param.u = uparam;
	}

	CanException(const char* _ecv_array msg, int32_t iparam) noexcept
		: message(msg)
	{
		param.i = iparam;
	}

	void GetMessage(const StringRef& reply) const noexcept;

	void DebugPrint() const noexcept;

	bool IsNull() const noexcept { return message == nullptr; }

  private:
	const char* _ecv_array _ecv_null message;
	union
	{
		int32_t i;
		uint32_t u;
	} param;
	String<StringLength50> stringParam;
};

// Functions to create and throw an exception. Using these avoids allocating the CanException object on the local stack
// when it is not going to be used.
[[noreturn]] void __attribute__((noinline)) ThrowCANException(const char* _ecv_array errMsg) THROWS(CanException);
[[noreturn]] void __attribute__((noinline)) ThrowCANException(const char* _ecv_array errMsg, uint32_t param)
	THROWS(CanException);

#endif /* SRC_GCODES_CANEXCEPTION_H_ */
