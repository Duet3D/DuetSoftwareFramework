/*
 * CanDriversData.cpp
 *
 *  Created on: 23 Dec 2021
 *      Author: David
 */

#include "CanDriversData.h"

#if SUPPORT_CAN_EXPANSION

// Insert a new entry, keeping the list ordered
void CanDriversList::AddEntry(DriverId driver) noexcept
{
	if (m_numEntries < ARRAY_SIZE(m_drivers))
	{
		// We could do a binary search here but the number of CAN drivers supported isn't huge, so linear search instead
		size_t insertPoint = 0;
		while (insertPoint < m_numEntries && m_drivers[insertPoint] < driver)
		{
			++insertPoint;
		}

		if (insertPoint == m_numEntries)
		{
			m_drivers[m_numEntries] = driver;
			++m_numEntries;
		}
		else if (m_drivers[insertPoint] != driver)
		{
			memmove(m_drivers + (insertPoint + 1),
					m_drivers + insertPoint,
					(m_numEntries - insertPoint) * sizeof(m_drivers[0]));
			m_drivers[insertPoint] = driver;
			++m_numEntries;
		}
	}
}

// Get the details of the drivers on the next board and advance startFrom beyond the entries for this board
CanAddress CanDriversList::GetNextBoardDriverBitmap(size_t& startFrom, CanDriversBitmap& driversBitmap) const noexcept
{
	driversBitmap.Clear();
	if (startFrom >= m_numEntries)
	{
		return CanId::NoAddress;
	}
	const CanAddress boardAddress = m_drivers[startFrom].boardAddress;
	do
	{
		driversBitmap.SetBit(m_drivers[startFrom].localDriver);
		++startFrom;
	} while (startFrom < m_numEntries && m_drivers[startFrom].boardAddress == boardAddress);
	return boardAddress;
}

#endif

// End
