/*
 * CanMessageGenericConstructor.h
 *
 *  Created on: 23 Jul 2019
 *      Author: David
 */

#ifndef SRC_CANMESSAGEGENERICCONSTRUCTOR_H_
#define SRC_CANMESSAGEGENERICCONSTRUCTOR_H_

#include "RepRapFirmware.h"

#if SUPPORT_CAN_EXPANSION

#  include <CanMessageFormats.h>
#  include <CanMessageGenericTableFormat.h>

class CanMessageGenericConstructor
{
  public:
	explicit CanMessageGenericConstructor(const ParamDescriptor* _ecv_array pParam) noexcept;

	// TODO add a method to populate from SPI message data

	// Methods to add parameters
	void AddU64Param(char c, uint64_t v) THROWS(CanException);
	void AddUParam(char c, uint32_t v) THROWS(CanException);
	void AddIParam(char c, int32_t v) THROWS(CanException);
	void AddFParam(char c, float v) THROWS(CanException);
	void AddCharParam(char c, char v) THROWS(CanException);
	void AddStringParam(char c, const char* _ecv_array v) THROWS(CanException);
	void AddDriverIdParam(char c, DriverId did) THROWS(CanException);
	void AddFloatArrayParam(char c, const float* _ecv_array v, size_t numV) THROWS(CanException);

	static GCodeResult SendAndGetResponse(CanMessageType msgType,
										  CanAddress dest,
										  const StringRef& reply,
										  const uint8_t* _ecv_null extra = nullptr) noexcept;

  private:
	// Return the correct position in the data to insert a parameter. If successful, add the bit to the parameter map
	// and pass back the expected parameter type and size; else throw.
	unsigned int FindInsertPoint(char c, ParamDescriptor::ParamType& t, size_t& sz) THROWS(CanException);

	// Append a value to the data, returning true if it wouldn't fit
	void StoreValue(const void* vp, size_t sz) THROWS(CanException);

	// Append a value to the data, returning true if it wouldn't fit
	template <class T>
	void StoreValue(const T& val) THROWS(CanException)
	{
		StoreValue(&val, sizeof(T));
	}

	// Insert a value in the data, returning true if it wouldn't fit
	void InsertValue(const void* vp, size_t sz, size_t pos) THROWS(CanException);

	const ParamDescriptor* const _ecv_array paramTable;
	size_t dataLen;
	CanMessageGeneric msg{};
};

#endif

#endif /* SRC_CANMESSAGEGENERICCONSTRUCTOR_H_ */
