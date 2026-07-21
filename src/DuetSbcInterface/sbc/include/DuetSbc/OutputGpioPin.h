// Driver for a single GPIO output line via the Linux GPIO character device.
// Ported from DuetSharedLibrary/OutputGpioPin.cs (v2 uAPI with v1 fallback). Used to expose a scope
// trigger that goes high while the SBC has data to transfer and low once the transfer completes.
#pragma once

#include <cstdint>
#include <string>

namespace Duet::Sbc
{

	class OutputGpioPin
	{
	  public:
		// Open a GPIO line for output and drive it to initialValue. Throws std::system_error on failure.
		OutputGpioPin(const std::string& devNode,
					  int line,
					  const std::string& consumerLabel,
					  bool initialValue = false);
		~OutputGpioPin();

		OutputGpioPin(const OutputGpioPin&) = delete;
		OutputGpioPin& operator=(const OutputGpioPin&) = delete;
		OutputGpioPin(OutputGpioPin&&) = delete;
		OutputGpioPin& operator=(OutputGpioPin&&) = delete;

		// Drive the line to the given level. Throws std::system_error on failure.
		void Write(bool value);

		[[nodiscard]] bool Value() const noexcept { return m_value; }

	  private:
		bool TryRequestLineV2(const std::string& consumerLabel);
		void RequestLineV1(const std::string& consumerLabel, bool initialValue);

		int m_chipFd = -1;
		int m_reqFd = -1;
		uint32_t m_offset;
		bool m_useV2 = false;
		bool m_value = false;
	};

} // namespace Duet::Sbc
