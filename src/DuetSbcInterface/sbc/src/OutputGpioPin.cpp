#include "DuetSbc/OutputGpioPin.h"

#include <fcntl.h>
#include <linux/gpio.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <system_error>

namespace Duet::Sbc
{

	OutputGpioPin::OutputGpioPin(const std::string& devNode,
								 int line,
								 const std::string& consumerLabel,
								 bool initialValue)
		: m_chipFd(::open(devNode.c_str(), O_RDWR))
		, m_offset(static_cast<uint32_t>(line))
		, m_value(initialValue)
	{

		if (m_chipFd < 0)
		{
			throw std::system_error(errno, std::generic_category(), "Cannot open GPIO device '" + devNode + "'");
		}

		try
		{
			m_useV2 = TryRequestLineV2(consumerLabel);
			if (!m_useV2)
			{
				RequestLineV1(consumerLabel, initialValue);
			}
			// Some drivers ignore the request-time default, so drive it explicitly
			Write(initialValue);
		}
		catch (...)
		{
			if (m_reqFd >= 0)
				::close(m_reqFd);
			if (m_chipFd >= 0)
				::close(m_chipFd);
			m_reqFd = m_chipFd = -1;
			throw;
		}
	}

	OutputGpioPin::~OutputGpioPin()
	{
		if (m_reqFd >= 0)
			::close(m_reqFd);
		if (m_chipFd >= 0)
			::close(m_chipFd);
		m_reqFd = m_chipFd = -1;
	}

	bool OutputGpioPin::TryRequestLineV2(const std::string& consumerLabel)
	{
		gpio_v2_line_request request{};
		std::memset(&request, 0, sizeof(request));
		request.num_lines = 1;
		request.offsets[0] = m_offset;
		request.config.flags = GPIO_V2_LINE_FLAG_OUTPUT;

		const size_t n = std::min(consumerLabel.size(), sizeof(request.consumer) - 1);
		std::memcpy(request.consumer, consumerLabel.data(), n);
		request.consumer[n] = 0;

		if (::ioctl(m_chipFd, GPIO_V2_GET_LINE_IOCTL, &request) < 0)
		{
			if (errno == ENOTTY || errno == EINVAL)
			{
				return false;
			}
			throw std::system_error(errno, std::generic_category(), "Cannot request GPIO output line via v2 uAPI");
		}
		m_reqFd = request.fd;
		return true;
	}

	void OutputGpioPin::RequestLineV1(const std::string& consumerLabel, bool initialValue)
	{
		gpiohandle_request request{};
		std::memset(&request, 0, sizeof(request));
		request.lines = 1;
		request.flags = GPIOHANDLE_REQUEST_OUTPUT;
		request.lineoffsets[0] = m_offset;
		request.default_values[0] = initialValue ? 1 : 0;

		const size_t n = std::min(consumerLabel.size(), sizeof(request.consumer_label) - 1);
		std::memcpy(request.consumer_label, consumerLabel.data(), n);
		request.consumer_label[n] = 0;

		if (::ioctl(m_chipFd, GPIO_GET_LINEHANDLE_IOCTL, &request) < 0)
		{
			throw std::system_error(errno, std::generic_category(), "Cannot request GPIO output line via v1 uAPI");
		}
		m_reqFd = request.fd;
	}

	void OutputGpioPin::Write(bool value)
	{
		if (m_reqFd < 0)
		{
			throw std::system_error(EBADF, std::generic_category(), "GPIO line is not configured");
		}

		if (m_useV2)
		{
			gpio_v2_line_values values{};
			std::memset(&values, 0, sizeof(values));
			values.mask = 1ULL;
			values.bits = value ? 1ULL : 0ULL;
			if (::ioctl(m_reqFd, GPIO_V2_LINE_SET_VALUES_IOCTL, &values) < 0)
			{
				throw std::system_error(errno, std::generic_category(), "Cannot write GPIO line (v2)");
			}
		}
		else
		{
			gpiohandle_data data{};
			std::memset(&data, 0, sizeof(data));
			data.values[0] = value ? 1 : 0;
			if (::ioctl(m_reqFd, GPIOHANDLE_SET_LINE_VALUES_IOCTL, &data) < 0)
			{
				throw std::system_error(errno, std::generic_category(), "Cannot write GPIO line (v1)");
			}
		}
		m_value = value;
	}

} // namespace Duet::Sbc
