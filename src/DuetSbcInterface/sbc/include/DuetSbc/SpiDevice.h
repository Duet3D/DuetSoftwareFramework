// Full-duplex SPI transfers via the spidev character device.
// Ported from DuetSharedLibrary/SpiDevice.cs.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace duet::sbc {

class SpiDevice {
public:
    // Open a spidev node (e.g. /dev/spidev0.0) and configure mode/word size/speed.
    // Throws std::system_error on failure.
    SpiDevice(const std::string &devNode, uint32_t speedHz, int transferMode);
    ~SpiDevice();

    SpiDevice(const SpiDevice &) = delete;
    SpiDevice &operator=(const SpiDevice &) = delete;

    // Perform a full-duplex transfer of `length` bytes. tx and rx may point at the same buffer.
    // Throws std::system_error on failure.
    void TransferFullDuplex(const uint8_t *tx, uint8_t *rx, size_t length);

private:
    int _fd = -1;
    uint32_t _speed;
};

} // namespace duet::sbc
