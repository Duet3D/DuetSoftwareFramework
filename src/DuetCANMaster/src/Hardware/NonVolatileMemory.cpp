/*
 * NonVolatileMemory.cpp
 *
 *  Created on: 24 Aug 2020
 *      Author: David
 */

#include "NonVolatileMemory.h"

#if SAM4E || SAM4S || SAME70
#  include <Cache.h>
#  include <Flash.h>
#  include <RTOSIface/RTOSIface.h>
#endif

NonVolatileMemory::NonVolatileMemory() noexcept
	: m_state(NvmState::NotRead)
{
}

void NonVolatileMemory::EnsureRead() noexcept
{
	if (m_state == NvmState::NotRead)
	{
#if SAME5x
		memcpyu32(reinterpret_cast<uint32_t*>(&buffer),
				  reinterpret_cast<const uint32_t*>(SEEPROM_ADDR),
				  sizeof(buffer) / sizeof(uint32_t));
#elif SAM4E || SAM4S || SAME70
		Flash::ReadUserSignature(reinterpret_cast<uint32_t*>(&m_buffer), sizeof(m_buffer) / sizeof(uint32_t));
#else
#  error Unsupported processor
#endif
		if (m_buffer.magic != NVM::MagicValue)
		{
			//			debugPrintf("Invalid user area\n");
			memset(&m_buffer, 0xFF, sizeof(m_buffer));
			m_buffer.magic = NVM::MagicValue;
			m_state = NvmState::EraseAndWriteNeeded;
		}
		else
		{
			m_state = NvmState::Clean;
			//			debugPrintf("user area valid\n");
		}
	}
}

void NonVolatileMemory::EnsureWritten() noexcept
{
#if SAME5x
	if (state >= NvmState::writeNeeded)
	{
		// No need to erase on the SAME5x because the EEPROM emulation manages it
		while (NVMCTRL->SEESTAT.bit.BUSY)
		{
		}
		memcpyu32(reinterpret_cast<uint32_t*>(SEEPROM_ADDR),
				  reinterpret_cast<const uint32_t*>(&buffer),
				  sizeof(buffer) / sizeof(uint32_t));
		state = NvmState::clean;
		while (NVMCTRL->SEESTAT.bit.BUSY)
		{
		}
	}
#else
	if (m_state == NvmState::EraseAndWriteNeeded)
	{
		// Erase the page
#  if SAM4E || SAM4S || SAME70
		Flash::EraseUserSignature();
#  endif
		m_state = NvmState::WriteNeeded;
	}

	if (m_state == NvmState::WriteNeeded)
	{
#  if SAM4E || SAM4S || SAME70
		const bool cacheEnabled = Cache::Disable();
		Flash::WriteUserSignature(reinterpret_cast<const uint32_t*>(&m_buffer));
		if (cacheEnabled)
		{
			Cache::Enable();
		}
#  else
#	error Unsupported processor
#  endif
		m_state = NvmState::Clean;
	}
#endif
}

SoftwareResetData* _ecv_null NonVolatileMemory::GetLastWrittenResetData(unsigned int& slot) noexcept
{
	EnsureRead();
	for (unsigned int i = NumberOfResetDataSlots; i != 0;)
	{
		--i;
		if (m_buffer.resetData[i].IsValid())
		{
			slot = i;
			return &m_buffer.resetData[i];
		}
	}
	return nullptr;
}

SoftwareResetData* NonVolatileMemory::AllocateResetDataSlot() noexcept
{
	EnsureRead();
	for (auto& i : m_buffer.resetData)
	{
		if (i.IsVacant())
		{
			if (m_state ==
				NvmState::Clean) // need this test because state may already be EraseAndWriteNeeded after EnsureRead
			{
				m_state = NvmState::WriteNeeded; // assume the caller will write to the allocated slot
			}
			return &i;
		}
	}

	// All slots are full, so clear them out and start again
	for (auto& i : m_buffer.resetData)
	{
		i.Clear();
	}
	m_state = NvmState::EraseAndWriteNeeded;
	return &m_buffer.resetData[0];
}

int8_t NonVolatileMemory::GetThermistorLowCalibration(unsigned int inputNumber) noexcept
{
	return GetThermistorCalibration(inputNumber, m_buffer.thermistorLowCalibration);
}

int8_t NonVolatileMemory::GetThermistorHighCalibration(unsigned int inputNumber) noexcept
{
	return GetThermistorCalibration(inputNumber, m_buffer.thermistorHighCalibration);
}

void NonVolatileMemory::SetThermistorLowCalibration(unsigned int inputNumber, int8_t val) noexcept
{
	SetThermistorCalibration(inputNumber, val, m_buffer.thermistorLowCalibration);
}

void NonVolatileMemory::SetThermistorHighCalibration(unsigned int inputNumber, int8_t val) noexcept
{
	SetThermistorCalibration(inputNumber, val, m_buffer.thermistorHighCalibration);
}

int8_t NonVolatileMemory::GetThermistorCalibration(unsigned int inputNumber, uint8_t* _ecv_array calibArray) noexcept
{
	EnsureRead();
	return (inputNumber >= MaxCalibratedThermistors || calibArray[inputNumber] == 0xFF)
			   ? 0
			   : (int)calibArray[inputNumber] - 0x7F;
}

void NonVolatileMemory::SetThermistorCalibration(unsigned int inputNumber,
												 int8_t val,
												 uint8_t* _ecv_array calibArray) noexcept
{
	if (inputNumber < MaxCalibratedThermistors)
	{
		EnsureRead();
		const uint8_t oldVal = calibArray[inputNumber];
		const uint8_t newVal = val + 0x7F;
		if (oldVal != newVal)
		{
			// If we are only changing 1 bits to 0 then we don't need to erase
			calibArray[inputNumber] = newVal;
			if ((newVal & ~oldVal) != 0)
			{
				m_state = NvmState::EraseAndWriteNeeded;
			}
			else if (m_state == NvmState::Clean)
			{
				m_state = NvmState::WriteNeeded;
			}
		}
	}
}

// End
