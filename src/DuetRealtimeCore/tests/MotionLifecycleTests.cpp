// The motion engine's lifecycle as DuetControlServer drives it through the C ABI.
//
// DuetControlServer starts the engine on a thread of its own, after pushing the machine description
// down, while the G-code channels are already running. A move can therefore be offered to the
// engine before its rings exist, and the engine has to answer that it has no room rather than read
// rings that have not been built.

#include "TestSupport.h"

#include <CApi.h>

#include <cstdio>
#include <string>
#include <unistd.h>

namespace
{
	// The socket transport, pointed at a path nothing listens on. The handle is never connected:
	// the motion engine does not need the link to be up to be started or asked for room
	DuetRTHandle* CreateUnconnected(const std::string& socketPath) noexcept
	{
		DuetRTConfig config{};
		DuetRT_DefaultConfig(&config);
		config.transport = 1;
		config.socketPath = socketPath.c_str();
		config.useRealtimeScheduling = 0;

		char error[256]{};
		DuetRTHandle* const handle = DuetRT_Create(&config, error, static_cast<int32_t>(sizeof(error)));
		if (handle == nullptr)
		{
			std::printf("FAIL: could not create the interface: %s\n", error);
		}
		return handle;
	}

	void TestNoRoomBeforeTheEngineStarts(const std::string& socketPath) noexcept
	{
		DuetRTHandle* const handle = CreateUnconnected(socketPath);
		CHECK(handle != nullptr, "the interface is created");
		if (handle == nullptr)
		{
			return;
		}

		CHECK(DuetRT_MotionCanAddMove(handle, 0) == 0, "a ring that has not been built has no room for a move");

		CHECK(DuetRT_MotionStart(handle, 0) == 1, "the engine starts");
		CHECK(DuetRT_MotionCanAddMove(handle, 0) == 1, "a started engine has room for a move");

		DuetRT_MotionStop(handle);
		DuetRT_Destroy(handle);
	}
}

int main()
{
	const std::string socketPath = "/tmp/duet-motion-lifecycle-" + std::to_string(getpid()) + ".sock";

	std::printf("Motion lifecycle:\n");
	TestNoRoomBeforeTheEngineStarts(socketPath);

	return TestSupport::Summarise("motion lifecycle");
}
