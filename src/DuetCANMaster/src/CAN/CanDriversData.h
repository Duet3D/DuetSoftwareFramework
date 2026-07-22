/*
 * CanDriversData.h
 *
 *  Created on: 23 Dec 2021
 *      Author: David
 */

#ifndef SRC_CAN_CANDRIVERSDATA_H_
#define SRC_CAN_CANDRIVERSDATA_H_

#include "RepRapFirmware.h"

#if SUPPORT_CAN_EXPANSION

using CanDriversBitmap = Bitmap<uint16_t>;

// Class to accumulate a set of values relating to CAN-connected drivers
template <class T>
class CanDriversData
{
  public:
	CanDriversData() noexcept;
	void AddEntry(DriverId id, T val) noexcept;
	size_t GetNumEntries() const noexcept { return m_numEntries; }
	CanAddress GetNextBoardDriverBitmap(size_t& startFrom, CanDriversBitmap& driversBitmap) const noexcept;
	T GetElement(size_t n) const noexcept pre(n < GetNumEntries()) { return m_data[n].val; }

  private:
	struct DriverDescriptor
	{
		DriverId driver;
		T val;
	};

	size_t m_numEntries;
	DriverDescriptor m_data[MaxCanDrivers];
};

// Class to represent a set of CAN-connected drivers with no associated data
class CanDriversList
{
  public:
	CanDriversList() noexcept
		: m_numEntries(0)
	{
	}
	void Clear() noexcept { m_numEntries = 0; }
	void AddEntry(DriverId driver) noexcept;
	size_t GetNumEntries() const noexcept { return m_numEntries; }
	bool IsEmpty() const noexcept { return m_numEntries == 0; }
	CanAddress GetNextBoardDriverBitmap(size_t& startFrom, CanDriversBitmap& driversBitmap) const noexcept;

  private:
	size_t m_numEntries;
	DriverId m_drivers[MaxCanDrivers];
};

// Members of template class CanDriversData
template <class T>
CanDriversData<T>::CanDriversData() noexcept
	: m_numEntries(0)
{
}

// Insert a new entry, keeping the list ordered by driver ID
template <class T>
void CanDriversData<T>::AddEntry(DriverId driver, T val) noexcept
{
	if (m_numEntries < ARRAY_SIZE(m_data))
	{
		// We could do a binary search here but the number of CAN drivers supported isn't huge, so linear search instead
		size_t insertPoint = 0;
		while (insertPoint < m_numEntries && m_data[insertPoint].driver < driver)
		{
			++insertPoint;
		}
		memmove(m_data + (insertPoint + 1), m_data + insertPoint, (m_numEntries - insertPoint) * sizeof(m_data[0]));
		m_data[insertPoint].driver = driver;
		m_data[insertPoint].val = val;
		++m_numEntries;
	}
}

// Get the details of the drivers on the next board and advance startFrom beyond the entries for this board
template <class T>
CanAddress CanDriversData<T>::GetNextBoardDriverBitmap(size_t& startFrom,
													   CanDriversBitmap& driversBitmap) const noexcept
{
	driversBitmap.Clear();
	if (startFrom >= m_numEntries)
	{
		return CanId::NoAddress;
	}
	const CanAddress boardAddress = m_data[startFrom].driver.boardAddress;
	do
	{
		driversBitmap.SetBit(m_data[startFrom].driver.localDriver);
		++startFrom;
	} while (startFrom < m_numEntries && m_data[startFrom].driver.boardAddress == boardAddress);
	return boardAddress;
}

#endif

#endif /* SRC_CAN_CANDRIVERSDATA_H_ */
