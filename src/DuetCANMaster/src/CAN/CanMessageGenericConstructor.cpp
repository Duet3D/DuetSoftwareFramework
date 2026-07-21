/*
 * CanMessageGenericConstructor.cpp
 *
 *  Created on: 23 Jul 2019
 *      Author: David
 */

#include "CanMessageGenericConstructor.h"
#include "Hardware/IoPorts.h"

#if SUPPORT_CAN_EXPANSION

#  include "CanMessageBuffer.h"

#  include "CanInterface.h"
#  include <limits>

#  define STRINGIZE2(_v) #_v
#  define STRINGIZE(_v) STRINGIZE2(_v)

CanMessageGenericConstructor::CanMessageGenericConstructor(const ParamDescriptor* _ecv_array pParam) noexcept
	: paramTable(pParam)
	, dataLen(0)
{
	msg.paramMap = 0;
}

// Append a value to the data, throwing if it wouldn't fit
void CanMessageGenericConstructor::StoreValue(const void* vp, size_t sz) THROWS(CanException)
{
	if (dataLen + sz > sizeof(msg.data))
	{
		throw "CAN message too long";
	}
	memcpy(msg.data + dataLen, vp, sz);
	dataLen += sz;
}

// Insert a value in the data, throwing if it wouldn't fit
void CanMessageGenericConstructor::InsertValue(const void* vp, size_t sz, size_t pos) THROWS(CanException)
{
	if (dataLen + sz > sizeof(msg.data))
	{
		throw "CAN message too long";
	}
	memmove(msg.data + pos + sz, msg.data + pos, dataLen - pos);
	memcpy(msg.data + pos, vp, sz);
	dataLen += sz;
}

// Return the correct position in the data to insert a parameter. If successful, add the bit to the parameter map and
// pass back the expect5ed parameter type; else throw.
unsigned int CanMessageGenericConstructor::FindInsertPoint(char c, ParamDescriptor::ParamType& t, size_t& sz)
	THROWS(CanException)
{
	unsigned int pos = 0;
	uint32_t paramBit = 1;
	for (const ParamDescriptor* _ecv_array d = paramTable; d->letter != 0; ++d)
	{
		const bool present = (msg.paramMap & paramBit) != 0;
		if (d->letter == c)
		{
			if (present)
			{
				throw "duplicate parameter";
			}
			msg.paramMap |= paramBit;
			t = d->type;
			sz = d->ItemSize();
			return pos;
		}

		if (present)
		{
			// This parameter is present, so skip it
			const size_t size = d->ItemSize();
			if (size != 0)
			{
				pos += size;
			}
			else
			{
				// The only item with size 0 is string, so skip up to and including the null terminator
				do
				{
				} while (msg.data[pos++] != 0);
			}
		}
		paramBit <<= 1;
	}
	throw "wrong parameter letter";
}

// TODO factor out the common code in the following several routines
void CanMessageGenericConstructor::AddU64Param(char c, uint64_t v) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	if (t != ParamDescriptor::uint64)
	{
		throw "u64val wrong parameter type";
	}
	InsertValue(&v, sz, pos);
}

void CanMessageGenericConstructor::AddUParam(char c, uint32_t v) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	switch (t)
	{
	case ParamDescriptor::uint32:
		break;

	case ParamDescriptor::uint16:
	case ParamDescriptor::pwmFreq:
		if (v >= (1u << 16))
		{
			throw "uval too large";
		}
		break;

	case ParamDescriptor::uint8:
		if (v >= (1u << 8))
		{
			throw "uval too large";
		}
		break;

	default:
		throw "uval wrong parameter type";
	}

	InsertValue(&v, sz, pos);
}

void CanMessageGenericConstructor::AddIParam(char c, int32_t v) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	switch (t)
	{
	case ParamDescriptor::int32:
		break;

	case ParamDescriptor::int16:
		if (v >= (int32_t)(1u << 15) || v < -(int32_t)(1u << 15))
		{
			throw "ival too large";
		}
		break;

	case ParamDescriptor::uint8:
		if (v >= (int32_t)(1u << 7) || v < -(int32_t)(1u << 7))
		{
			throw "ival too large";
		}
		break;

	default:
		throw "ival wrong parameter type";
	}

	InsertValue(&v, sz, pos);
}

void CanMessageGenericConstructor::AddFParam(char c, float v) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	if (t != ParamDescriptor::float_p)
	{
		throw "fval wrong parameter type";
	}
	InsertValue(&v, sz, pos);
}

void CanMessageGenericConstructor::AddCharParam(char c, char v) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	if (t != ParamDescriptor::char_p)
	{
		throw "cval wrong parameter type";
	}
	InsertValue(&v, sz, pos);
}

void CanMessageGenericConstructor::AddStringParam(char c, const char* _ecv_array v) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	switch (t)
	{
	case ParamDescriptor::string:
	case ParamDescriptor::reducedString: // TODO currently we don't reduce the string, but it should already be reduced
		InsertValue(v, strlen(v) + 1, pos);
		break;

	default:
		throw "sval wrong parameter type";
	}
}

void CanMessageGenericConstructor::AddDriverIdParam(char c, DriverId did) THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	if (t != ParamDescriptor::localDriver)
	{
		throw "didval wrong parameter type";
	}

	InsertValue(&did.localDriver, sz, pos);
}

void CanMessageGenericConstructor::AddFloatArrayParam(char c, const float* _ecv_array v, size_t numV)
	THROWS(CanException)
{
	ParamDescriptor::ParamType t{};
	size_t sz = 0;
	const unsigned int pos = FindInsertPoint(c, t, sz);
	if (t != ParamDescriptor::float_array || numV != sz)
	{
		throw "fval array wrong parameter type or length";
	}
	InsertValue(&numV, sizeof(uint8_t), pos);
	InsertValue(v, numV * sizeof(float), pos + sizeof(uint8_t));
}

GCodeResult CanMessageGenericConstructor::SendAndGetResponse(CanMessageType msgType,
															 CanAddress dest,
															 const StringRef& reply,
															 const uint8_t* _ecv_null extra) noexcept
{
	// In SBC bridge mode this firmware no longer performs synchronous CAN request/reply transactions; the SBC owns
	// request/reply correlation and drives generic CAN messages itself via CANRequestHeader.
	(void)msgType;
	(void)dest;
	(void)extra;
	reply.copy("generic CAN messages with a reply are handled by the SBC, not the firmware");
	return GCodeResult::error;
}

#endif

// End
