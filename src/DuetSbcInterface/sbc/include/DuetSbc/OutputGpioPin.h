// Driver for a single GPIO output line via the Linux GPIO character device.
// Ported from DuetSharedLibrary/OutputGpioPin.cs (v2 uAPI with v1 fallback). Used to expose a scope
// trigger that goes high while the SBC has data to transfer and low once the transfer completes.
#pragma once

#include <cstdint>
#include <string>

namespace duet::sbc {

class OutputGpioPin {
public:
    // Open a GPIO line for output and drive it to initialValue. Throws std::system_error on failure.
    OutputGpioPin(const std::string &devNode, int line, const std::string &consumerLabel,
                  bool initialValue = false);
    ~OutputGpioPin();

    OutputGpioPin(const OutputGpioPin &) = delete;
    OutputGpioPin &operator=(const OutputGpioPin &) = delete;

    // Drive the line to the given level. Throws std::system_error on failure.
    void Write(bool value);

    bool Value() const noexcept { return _value; }

private:
    bool TryRequestLineV2(const std::string &consumerLabel);
    void RequestLineV1(const std::string &consumerLabel, bool initialValue);

    int _chipFd = -1;
    int _reqFd = -1;
    uint32_t _offset;
    bool _useV2 = false;
    bool _value = false;
};

} // namespace duet::sbc
