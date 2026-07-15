#include "DuetSbc/GpioInputPin.h"

#include <fcntl.h>
#include <linux/gpio.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <system_error>

namespace duet::sbc {

GpioInputPin::GpioInputPin(const std::string &devNode, int line, const std::string &consumerLabel)
    : _offset(static_cast<uint32_t>(line)) {
    _chipFd = ::open(devNode.c_str(), O_RDONLY);
    if (_chipFd < 0) {
        throw std::system_error(errno, std::generic_category(), "Cannot open GPIO device '" + devNode + "'");
    }

    try {
        _useV2 = TryRequestLineV2(consumerLabel);
        if (!_useV2) {
            RequestLineV1(consumerLabel);
        }
        // Non-blocking so ReadEvent() can drain the queue without blocking; waiting is done via poll().
        const int flags = ::fcntl(_reqFd, F_GETFL, 0);
        if (flags >= 0) {
            ::fcntl(_reqFd, F_SETFL, flags | O_NONBLOCK);
        }
        Read();
    } catch (...) {
        if (_reqFd >= 0) ::close(_reqFd);
        if (_chipFd >= 0) ::close(_chipFd);
        _reqFd = _chipFd = -1;
        throw;
    }
}

GpioInputPin::~GpioInputPin() {
    if (_reqFd >= 0) ::close(_reqFd);
    if (_chipFd >= 0) ::close(_chipFd);
    _reqFd = _chipFd = -1;
}

bool GpioInputPin::TryRequestLineV2(const std::string &consumerLabel) {
    gpio_v2_line_request request;
    std::memset(&request, 0, sizeof(request));
    request.num_lines = 1;
    request.offsets[0] = _offset;
    request.config.flags = GPIO_V2_LINE_FLAG_INPUT | GPIO_V2_LINE_FLAG_EDGE_RISING | GPIO_V2_LINE_FLAG_EDGE_FALLING;

    const size_t n = std::min(consumerLabel.size(), sizeof(request.consumer) - 1);
    std::memcpy(request.consumer, consumerLabel.data(), n);
    request.consumer[n] = 0;

    if (::ioctl(_chipFd, GPIO_V2_GET_LINE_IOCTL, &request) < 0) {
        // ENOTTY/EINVAL means the kernel predates the v2 uAPI; fall back to v1
        if (errno == ENOTTY || errno == EINVAL) {
            return false;
        }
        throw std::system_error(errno, std::generic_category(), "Cannot request GPIO line via v2 uAPI");
    }
    _reqFd = request.fd;
    return true;
}

void GpioInputPin::RequestLineV1(const std::string &consumerLabel) {
    gpioevent_request request;
    std::memset(&request, 0, sizeof(request));
    request.lineoffset = _offset;
    request.handleflags = GPIOHANDLE_REQUEST_INPUT;
    request.eventflags = GPIOEVENT_REQUEST_BOTH_EDGES;

    const size_t n = std::min(consumerLabel.size(), sizeof(request.consumer_label) - 1);
    std::memcpy(request.consumer_label, consumerLabel.data(), n);
    request.consumer_label[n] = 0;

    if (::ioctl(_chipFd, GPIO_GET_LINEEVENT_IOCTL, &request) < 0) {
        throw std::system_error(errno, std::generic_category(), "Cannot request GPIO line via v1 uAPI");
    }
    _reqFd = request.fd;
}

bool GpioInputPin::Read() {
    bool value;
    if (_useV2) {
        gpio_v2_line_values values;
        std::memset(&values, 0, sizeof(values));
        values.mask = 1ULL;
        if (::ioctl(_reqFd, GPIO_V2_LINE_GET_VALUES_IOCTL, &values) < 0) {
            throw std::system_error(errno, std::generic_category(), "Cannot read GPIO line (v2)");
        }
        value = (values.bits & 1ULL) != 0;
    } else {
        gpiohandle_data data;
        std::memset(&data, 0, sizeof(data));
        if (::ioctl(_reqFd, GPIOHANDLE_GET_LINE_VALUES_IOCTL, &data) < 0) {
            throw std::system_error(errno, std::generic_category(), "Cannot read GPIO line (v1)");
        }
        value = data.values[0] != 0;
    }
    return value;
}

bool GpioInputPin::ReadEvent() {
    if (_useV2) {
        gpio_v2_line_event ev;
        const ssize_t n = ::read(_reqFd, &ev, sizeof(ev));
        if (n < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                return false;
            }
            throw std::system_error(errno, std::generic_category(), "GPIO event read failed (v2)");
        }
        if (n != static_cast<ssize_t>(sizeof(ev))) {
            return false;
        }
        // line_seqno increments by 1 per edge; a larger gap means dropped edges
        if (_haveSeqno && ev.line_seqno > _lastSeqno + 1) {
            _missedEdges += static_cast<int>(ev.line_seqno - _lastSeqno - 1);
        }
        _lastSeqno = ev.line_seqno;
        _haveSeqno = true;
        if (ev.id == GPIO_V2_LINE_EVENT_RISING_EDGE) {
            _lastRisingSeqno = ev.line_seqno;
        }
        return true;
    }

    gpioevent_data ev;
    const ssize_t n = ::read(_reqFd, &ev, sizeof(ev));
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            return false;
        }
        throw std::system_error(errno, std::generic_category(), "GPIO event read failed (v1)");
    }
    if (n != static_cast<ssize_t>(sizeof(ev))) {
        return false;
    }
    // v1 carries no sequence number, so count rising edges ourselves
    if (ev.id == GPIOEVENT_EVENT_RISING_EDGE) {
        _lastRisingSeqno++;
    }
    return true;
}

} // namespace duet::sbc
