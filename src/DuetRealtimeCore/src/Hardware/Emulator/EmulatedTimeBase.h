// The SBC's local time base when its controller is emulated: emulated time rather than CLOCK_MONOTONIC.
//
// StepTimer models the controller's step clock against the SBC's local time base, and rejects a fit
// whose rate is far from nominal. An emulator runs slower than real time, and not at a steady rate, so
// against CLOCK_MONOTONIC the step clock's rate would be a fraction of nominal. Each frame from the
// emulator carries the emulated time it was sent at (EmulatorLinkFormats.h), and between frames this
// extrapolates at the rate emulated time has recently passed, never going backwards and never running
// far ahead of the last frame.
#pragma once

#include <cstdint>
#include <mutex>

namespace Duet::Sbc::Emulator
{

	class EmulatedTimeBase
	{
	  public:
		// The time base the emulator link feeds. Process-wide, like the CLOCK_MONOTONIC it stands in for.
		static EmulatedTimeBase& Instance() noexcept;

		// Make StepTimer read this time base, or CLOCK_MONOTONIC again
		static void Install() noexcept;
		static void Uninstall() noexcept;

		// Forget every sample, as when a new emulator connects
		void Reset() noexcept;

		// A frame sent at `emulatedNs` of emulated time arrived at `monotonicNs` of CLOCK_MONOTONIC
		void OnSample(int64_t emulatedNs, int64_t monotonicNs) noexcept;

		// Emulated time at `monotonicNs` of CLOCK_MONOTONIC, in nanoseconds; 0 before the first sample
		int64_t At(int64_t monotonicNs) noexcept;

	  private:
		// How far ahead of the last frame the time base may run: past this, the emulator has fallen
		// behind and the time base waits for it rather than guess
		static constexpr int64_t s_maxExtrapolationNs = 20000000;

		// The shortest real time over which the rate is measured; shorter spans are too noisy
		static constexpr int64_t s_minRateSpanNs = 20000000;

		// Weight of a new rate measurement against the running estimate
		static constexpr double s_rateWeight = 0.3;

		static int64_t ReadClock() noexcept;

		std::mutex m_mutex;
		bool m_haveSample = false;
		int64_t m_lastEmulatedNs = 0;
		int64_t m_lastMonotonicNs = 0;
		int64_t m_rateEmulatedNs = 0;
		int64_t m_rateMonotonicNs = 0;
		double m_rate = 0.0; // emulated ns per real ns; 0 until measured
		int64_t m_lastReturnedNs = 0;
	};

} // namespace Duet::Sbc::Emulator
