#include "SpiDevice.h"

#include <fcntl.h>
#include <linux/spi/spidev.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <cerrno>
#include <cstring>
#include <system_error>

namespace Duet::Sbc
{

	SpiDevice::SpiDevice(const std::string& devNode, uint32_t speedHz, int transferMode)
		: m_fd(::open(devNode.c_str(), O_RDWR))
		, m_speed(speedHz)
	{

		if (m_fd < 0)
		{
			throw std::system_error(errno, std::generic_category(), "Cannot open SPI device '" + devNode + "'");
		}

		try
		{
			uint8_t mode = 0;
			switch (transferMode)
			{
			case 0:
				mode = SPI_MODE_0;
				break;
			case 1:
				mode = SPI_MODE_1;
				break;
			case 2:
				mode = SPI_MODE_2;
				break;
			case 3:
				mode = SPI_MODE_3;
				break;
			default:
				throw std::system_error(EINVAL, std::generic_category(), "SPI transfer mode must be between 0 and 3");
			}
			if (::ioctl(m_fd, SPI_IOC_WR_MODE, &mode) < 0)
			{
				throw std::system_error(errno, std::generic_category(), "Cannot set SPI mode");
			}

			uint8_t bitsPerWord = 8;
			if (::ioctl(m_fd, SPI_IOC_WR_BITS_PER_WORD, &bitsPerWord) < 0)
			{
				throw std::system_error(errno, std::generic_category(), "Cannot set SPI word size to 8 bits");
			}

			if (::ioctl(m_fd, SPI_IOC_WR_MAX_SPEED_HZ, &m_speed) < 0)
			{
				throw std::system_error(errno, std::generic_category(), "Cannot set SPI speed");
			}
		}
		catch (...)
		{
			::close(m_fd);
			m_fd = -1;
			throw;
		}
	}

	SpiDevice::~SpiDevice()
	{
		if (m_fd >= 0)
		{
			::close(m_fd);
			m_fd = -1;
		}
	}

	void SpiDevice::TransferFullDuplex(const uint8_t* tx, const uint8_t* rx, size_t length) const
	{
		spi_ioc_transfer transfer{};
		std::memset(&transfer, 0, sizeof(transfer));
		transfer.tx_buf = reinterpret_cast<uint64_t>(tx);
		transfer.rx_buf = reinterpret_cast<uint64_t>(rx);
		transfer.len = static_cast<uint32_t>(length);
		transfer.speed_hz = m_speed;
		transfer.bits_per_word = 8;

		if (::ioctl(m_fd, SPI_IOC_MESSAGE(1), &transfer) < 1)
		{
			throw std::system_error(errno, std::generic_category(), "SPI transfer failed");
		}
	}

} // namespace Duet::Sbc
