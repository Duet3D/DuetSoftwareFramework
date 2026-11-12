#include "EmulatedTimeBase.h"

#include <Motion/StepTimer.h>

#include <algorithm>
#include <ctime>

namespace Duet::Sbc::Emulator
{
	namespace
	{
		int64_t MonotonicNs() noexcept
		{
			timespec ts{};
			clock_gettime(CLOCK_MONOTONIC, &ts);
			return (int64_t)ts.tv_sec * 1000000000 + ts.tv_nsec;
		}
	} // namespace

	EmulatedTimeBase& EmulatedTimeBase::Instance() noexcept
	{
		static EmulatedTimeBase s_instance;
		return s_instance;
	}

	void EmulatedTimeBase::Install() noexcept
	{
		::StepTimer::SetLocalClockSource(ReadClock);
	}

	void EmulatedTimeBase::Uninstall() noexcept
	{
		::StepTimer::SetLocalClockSource(nullptr);
	}

	int64_t EmulatedTimeBase::ReadClock() noexcept
	{
		return Instance().At(MonotonicNs());
	}

	void EmulatedTimeBase::Reset() noexcept
	{
		const std::lock_guard lock(m_mutex);
		m_haveSample = false;
		m_rate = 0.0;
		m_lastReturnedNs = 0;
	}

	void EmulatedTimeBase::OnSample(int64_t emulatedNs, int64_t monotonicNs) noexcept
	{
		const std::lock_guard lock(m_mutex);
		if (!m_haveSample)
		{
			m_haveSample = true;
			m_rateEmulatedNs = emulatedNs;
			m_rateMonotonicNs = monotonicNs;
		}
		else if (monotonicNs - m_rateMonotonicNs >= s_minRateSpanNs)
		{
			const double measured =
				(double)(emulatedNs - m_rateEmulatedNs) / (double)(monotonicNs - m_rateMonotonicNs);
			m_rate = (m_rate == 0.0) ? measured : (s_rateWeight * measured) + ((1.0 - s_rateWeight) * m_rate);
			m_rateEmulatedNs = emulatedNs;
			m_rateMonotonicNs = monotonicNs;
		}
		m_lastEmulatedNs = emulatedNs;
		m_lastMonotonicNs = monotonicNs;
	}

	int64_t EmulatedTimeBase::At(int64_t monotonicNs) noexcept
	{
		const std::lock_guard lock(m_mutex);
		if (!m_haveSample)
		{
			return m_lastReturnedNs;
		}
		const auto extrapolated = (int64_t)((double)std::max<int64_t>(0, monotonicNs - m_lastMonotonicNs) * m_rate);
		const int64_t estimate = m_lastEmulatedNs + std::min(extrapolated, s_maxExtrapolationNs);
		m_lastReturnedNs = std::max(m_lastReturnedNs, estimate);
		return m_lastReturnedNs;
	}

} // namespace Duet::Sbc::Emulator
