#include "ProcessHelpers.h"

#include <sched.h>

#include <algorithm>
#include <cstring>
#include <fstream>
#include <string>

namespace Duet::Sbc
{

	bool IsRaspberryPi() noexcept
	{
		try
		{
			std::ifstream f("/proc/cpuinfo");
			std::string line;
			while (std::getline(f, line))
			{
				if (line.rfind("Hardware", 0) == 0 && line.find("BCM") != std::string::npos)
				{
					return true;
				}
				if (line.rfind("Model", 0) == 0 && line.find("Raspberry Pi") != std::string::npos)
				{
					return true;
				}
			}
		}
		catch (...)
		{
			// @intentional: not on Linux, or /proc/cpuinfo unavailable; the caller has a default
		}
		return false;
	}

	bool PinCurrentThreadToCore(int coreId) noexcept
	{
		cpu_set_t set;
		CPU_ZERO(&set);
		CPU_SET(coreId, &set);
		// pid 0 targets the calling thread
		return sched_setaffinity(0, sizeof(set), &set) == 0;
	}

	bool SetCurrentThreadRealtimePriority(int priority) noexcept
	{
		const int lo = sched_get_priority_min(SCHED_FIFO);
		const int hi = sched_get_priority_max(SCHED_FIFO);
		if (lo >= 0 && hi >= lo)
		{
			priority = std::clamp(priority, lo, hi);
		}

		sched_param param{};
		std::memset(&param, 0, sizeof(param));
		param.sched_priority = priority;
		return sched_setscheduler(0, SCHED_FIFO, &param) == 0;
	}

} // namespace Duet::Sbc
