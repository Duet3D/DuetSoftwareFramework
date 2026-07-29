"""
High Level Analyzer for the DuetSoftwareFramework SBC <-> RepRapFirmware SPI link.

This sits on top of the built-in Saleae "SPI" analyzer and decodes the packet
framing of the SBC interface:

  * Transfer headers        (16 bytes, protocol >= 4 / 12 bytes, protocol < 4)
  * Header / data responses (4 bytes)
  * Data packet headers      (8 bytes each, with 4-byte padding between packets)

It decodes the *headers* only - the payload that follows each packet header is
left untouched, exactly as requested.

Both directions are decoded from a single full-duplex exchange:
  * MOSI  = SBC (DuetControlServer) -> controller (DuetCANMaster)
  * MISO  = controller -> SBC

Wiring / SPI-analyzer requirements
----------------------------------
Each sub-exchange of a transfer is framed by its own chip-select assertion, so
the underlying SPI analyzer MUST have its "Enable" (CS) line connected. The HLA
uses the enable/disable frames to know where one sub-exchange ends and the next
begins. Configure the SPI analyzer for 8 bits per transfer, MSB first.

Protocol source of truth:
  src/DuetControlServer/Link/Protocol/Shared/TransferHeader.cs
  src/DuetControlServer/Link/Protocol/Shared/TransferResponse.cs
  src/DuetControlServer/Link/Protocol/Shared/PacketHeader.cs
  src/DuetControlServer/Link/Protocol/Shared/Consts.cs
  lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h
"""

import struct

from saleae.analyzers import HighLevelAnalyzer, AnalyzerFrame, ChoicesSetting


# --- Protocol constants (mirror Consts.cs) --------------------------------

FORMAT_CODE = 0x5F             # Consts.FormatCode           (SBC mode)
FORMAT_CODE_STANDALONE = 0x60  # Consts.FormatCodeStandalone
INVALID_FORMAT_CODE = 0xC9     # Consts.InvalidFormatCode

HEADER_SIZE_V4 = 16            # protocol >= 4 (CRC32)
HEADER_SIZE_LEGACY = 12       # protocol <  4 (CRC16)
RESPONSE_SIZE = 4
PACKET_HEADER_SIZE = 8

# MessageFormats.h - ScheduleMove payload layout
SCHEDULE_MOVE_HEADER_SIZE = 56
SCHEDULE_MOVE_DRIVER_SIZE = 12
MAX_SCHEDULE_MOVE_DRIVERS = 32

# TransferResponse.cs
RESPONSE_NAMES = {
    1: "Success",
    2: "BadFormat",
    3: "BadProtocolVersion",
    4: "BadDataLength",
    5: "BadHeaderChecksum",
    6: "BadDataChecksum",
    0xFEFEFEFE: "BadResponse",
    0x00000000: "LowPin(0)",
    0xFFFFFFFF: "HighPin(0xFFFFFFFF)",
}

# SbcRequests/Request.cs - packets travelling SBC -> controller (MOSI)
SBC_REQUESTS = {
    0: "EmergencyStop",
    1: "Reset",
    2: "ConfigCAN",
    3: "EnableCAN",
    4: "ScheduleMove",
    5: "SendCANMessage",
    6: "WriteIap",
    7: "StartIap",
    8: "Message",
}

# FirmwareRequests/Request.cs - packets travelling controller -> SBC (MISO)
FIRMWARE_REQUESTS = {
    0: "ResendPacket",
    2: "CodeBufferUpdate",
    3: "Message",
    4: "MasterClock",
    5: "CANResponse",
    6: "MotionStopped",
}

# MessageTypeFlags.cs - bitmap of message destinations / flags
MESSAGE_TYPE_FLAGS = [
    (0x01, "Http"), (0x02, "Telnet"), (0x04, "File"), (0x08, "Usb"),
    (0x10, "Aux"), (0x20, "Trigger"), (0x40, "CodeQueue"), (0x80, "Lcd"),
    (0x100, "Sbc"), (0x200, "Daemon"), (0x400, "Aux2"), (0x800, "AutoPause"),
    (0x1000, "File2"), (0x2000, "Queue2"), (0x4000, "Usb2"), (0x8000, "Mqtt"),
    (0x10000, "BlockingUsb"), (0x20000, "ImmediateAux"),
    (0x1000000, "Error"), (0x2000000, "Warning"), (0x8000000, "Raw"),
    (0x10000000, "BinaryCodeReply"), (0x20000000, "Push"),
    (0x40000000, "LogLow"), (0x80000000, "LogHigh"),
]

# ScheduleMoveFlags (MessageFormats.h) - bits of ScheduleMoveHeader::flags
SCHEDULE_MOVE_FLAGS = [
    (0x01, "InputShaping"), (0x02, "PressureAdvance"),
    (0x04, "CheckEndstops"), (0x08, "LastPacket"),
]

# CanStatus.cs - reason a CAN reply was delivered
CAN_STATUS = {0: "Ok", 1: "Timeout", 2: "BusError", 3: "NoBuffer", 4: "Overflow"}

# CanMessageType.cs - value placed in the CAN id (active entries only)
CAN_MESSAGE_TYPES = {
    0: "EmergencyStop", 10: "Startup", 20: "ControlledStop", 30: "TimeSync",
    40: "PowerFailing", 45: "StopMovement", 46: "InsertHiccup", 47: "RevertPosition",
    52: "MovementLinearShaped", 102: "Event", 104: "EnterTestMode",
    105: "InputStateChangedV1", 106: "InputStateChangedV2",
    2010: "SetAddressAndNormalTiming", 2011: "SetFastTiming", 2012: "Reset",
    4012: "WriteGpio", 4013: "ReadInputsRequest", 4014: "StartAccelerometer",
    4015: "StartClosedLoopDataCollection", 4510: "StandardReply",
    4511: "BoardStatusReportV0", 4512: "AnnounceV0", 4514: "SensorTemperaturesReport",
    4515: "HeatersStatusReport", 4517: "FansReport", 4518: "ReadInputsReplyV0",
    4519: "DriversStatusReport", 4521: "HeaterTuningReport", 4522: "AccelerometerData",
    4523: "ClosedLoopData", 4524: "LogMessage", 4525: "AnnounceV1", 4526: "DebugText",
    4528: "FilamentMonitorsStatusReportV2", 4529: "ReadInputsReplyV1",
    4530: "BoardStatusReportV1", 4531: "HeaterModelReport",
    5000: "FirmwareBlockRequest", 5001: "FirmwareBlockResponse",
    6015: "SetDateTime", 6016: "UpdateDeltaParameters", 6018: "M569",
    6019: "FanParameters", 6020: "M915", 6023: "SetDriverStates", 6024: "ReturnInfo",
    6025: "UpdateFirmware", 6026: "M950Heater", 6027: "M950Fan", 6028: "M950Gpio",
    6029: "SetFanSpeed", 6030: "SetHeaterFaultDetection", 6031: "M308V1",
    6032: "HeaterTuningCommand", 6034: "AccelerometerConfig", 6035: "M950Led",
    6038: "AcknowledgeAnnounce", 6039: "SetHeaterMonitors", 6040: "DiagnosticTest",
    6041: "M569P1", 6042: "SetStepsPerMmAndMicrostepping", 6043: "SetMotorCurrents",
    6044: "SetPressureAdvanceV1", 6045: "SetStandstillCurrentFactor",
    6046: "CreateFilamentMonitor", 6047: "DeleteFilamentMonitor",
    6048: "ConfigureFilamentMonitor", 6050: "M569P2", 6051: "M569P6", 6052: "M569P7",
    6055: "WriteLedStrip", 6056: "M569P4", 6059: "TestReport",
    6060: "CreateInputMonitorV1", 6061: "ChangeInputMonitorV1", 6062: "SetInputShapingV1",
    6063: "HeaterFeedForwardV1", 6064: "M655", 6065: "EnableStallEndstop", 6066: "M111",
    6067: "SetDefaultHeaterModel", 6068: "SetHeaterTemperatureV1", 6069: "HeaterModelV3",
    6070: "SetPressureAdvanceV2", 0xFFFF: "UnusedMessageType",
}


def _can_type(value):
    return CAN_MESSAGE_TYPES.get(value, "0x%X" % value)


def _message_flags(value):
    dests = [name for bit, name in MESSAGE_TYPE_FLAGS if value & bit]
    return "|".join(dests) if dests else "NoDestination"


def _schedule_move_flags(value):
    names = [name for bit, name in SCHEDULE_MOVE_FLAGS if value & bit]
    unknown = value & ~sum(bit for bit, _ in SCHEDULE_MOVE_FLAGS)
    if unknown:
        names.append("0x%02X" % unknown)
    return "|".join(names) if names else "none"


# --- Per-request payload header parsers -----------------------------------
#
# Each parser reads the payload header that immediately follows an 8-byte
# packet header and returns (size, type_name, detail_string), or a list of such
# tuples for consecutive sub-structs. `off` points at the first payload byte.
# Structs are in SbcRequests/, FirmwareRequests/, Shared/ and MessageFormats.h.
# Requests without a defined payload header (EmergencyStop, Reset, WriteIap,
# StartIap, MotionStopped, ResendPacket) have no parser.

def _p_config_can(buf, off):
    return 4, "ConfigCanHeader", "ch=%d fd=%d rateMul=%d" % (
        buf[off], buf[off + 1], buf[off + 2])


def _p_enable_can(buf, off):
    return 4, "EnableCanHeader", "ch=%d enable=%d" % (buf[off], buf[off + 1])


def _p_send_can(buf, off):
    return 12, "SendCanMessageHeader", "token=%d type=%s reply=%d len=%d dst=%d isResp=%d" % (
        _u16(buf, off), _can_type(_u16(buf, off + 2)), _u16(buf, off + 4),
        buf[off + 6], buf[off + 7], buf[off + 8] & 0x01)


def _p_can_response(buf, off):
    return 12, "CanResponseHeader", "token=%d type=%s len=%d src=%d flags=0x%02X status=%s" % (
        _u16(buf, off), _can_type(_u16(buf, off + 2)), _u16(buf, off + 4),
        buf[off + 6], buf[off + 7], CAN_STATUS.get(buf[off + 8], str(buf[off + 8])))


def _p_schedule_move(buf, off):
    """ScheduleMoveHeader (56 bytes) plus the ScheduleMoveDriver records after it."""
    num_drivers = buf[off + 52]
    flags = buf[off + 53]
    out = [(SCHEDULE_MOVE_HEADER_SIZE, "ScheduleMoveHeader",
            "move=%d drivers=%d flags=%s when=%d clocks=%d/%d/%d "
            "dist=%.3f(accel %.3f, decel from %.3f) "
            "v=%.5f/%.5f/%.5f a=%.3e d=%.3e" % (
                _u32(buf, off + 48), num_drivers, _schedule_move_flags(flags),
                _u32(buf, off), _u32(buf, off + 4), _u32(buf, off + 8), _u32(buf, off + 12),
                _f32(buf, off + 24), _f32(buf, off + 28), _f32(buf, off + 32),
                _f32(buf, off + 36), _f32(buf, off + 40), _f32(buf, off + 44),
                _f32(buf, off + 16), _f32(buf, off + 20)))]

    driver_off = off + SCHEDULE_MOVE_HEADER_SIZE
    for _ in range(min(num_drivers, MAX_SCHEDULE_MOVE_DRIVERS)):
        is_extruder = buf[driver_off + 2]
        amount = ("extrusion=%.4f" % _f32(buf, driver_off + 8) if is_extruder
                  else "steps=%d" % _i32(buf, driver_off + 4))
        out.append((SCHEDULE_MOVE_DRIVER_SIZE, "ScheduleMoveDriver",
                    "board=%d driver=%d %s%s" % (
                        buf[driver_off], buf[driver_off + 1], amount,
                        " (extruder)" if is_extruder else "")))
        driver_off += SCHEDULE_MOVE_DRIVER_SIZE
    return out


def _p_message(buf, off):
    return 8, "MessageHeader", "type=%s len=%d" % (
        _message_flags(_u32(buf, off)), _u16(buf, off + 4))


def _p_code_buffer_update(buf, off):
    return 4, "CodeBufferUpdateHeader", "bufferSpace=%d" % _u16(buf, off)


def _p_master_clock(buf, off):
    return 8, "MasterClockHeader", "clock=%dms hiccup=%dms" % (
        _u32(buf, off), _u32(buf, off + 4))


# (is_mosi, request value) -> payload header parser
PAYLOAD_PARSERS = {
    # SBC -> controller (MOSI, SbcRequests)
    (True, 2): _p_config_can,       # ConfigCAN
    (True, 3): _p_enable_can,       # EnableCAN
    (True, 4): _p_schedule_move,    # ScheduleMove
    (True, 5): _p_send_can,         # SendCANMessage
    (True, 8): _p_message,          # Message
    # controller -> SBC (MISO, FirmwareRequests)
    (False, 2): _p_code_buffer_update,  # CodeBufferUpdate
    (False, 3): _p_message,             # Message
    (False, 4): _p_master_clock,        # MasterClock
    (False, 5): _p_can_response,        # CANResponse
}


def _u16(buf, off):
    return buf[off] | (buf[off + 1] << 8)


def _u32(buf, off):
    return buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24)


def _i32(buf, off):
    return struct.unpack_from("<i", buf, off)[0]


def _f32(buf, off):
    return struct.unpack_from("<f", buf, off)[0]


def _pad4(n):
    """Round up to the next 4-byte boundary (matches Reader.AddPadding)."""
    return (n + 3) & ~3


class Hla(HighLevelAnalyzer):
    # Which direction to annotate. SPI is full-duplex, so both streams occupy the
    # same byte times; a single Saleae HLA can only emit one non-overlapping,
    # byte-aligned stream, so pick one line per instance. Add the analyzer twice
    # (one MOSI, one MISO) to see both.
    direction = ChoicesSetting(
        label="Direction to decode",
        choices=("MOSI (SBC->RRF)", "MISO (RRF->SBC)"),
    )

    result_types = {
        "header": {
            "format": "HDR fmt={{data.format}} pkts={{data.numPackets}} "
                      "v{{data.protocol}} seq={{data.seq}} dataLen={{data.dataLength}} "
                      "crcHdr={{data.crcHeader}}",
        },
        "response": {
            "format": "RESP {{data.name}}",
        },
        "packet": {
            "format": "PKT {{data.request}} id={{data.id}} "
                      "len={{data.length}}{{data.resend}}",
        },
        "subheader": {
            "format": "{{data.name}} {{data.detail}}",
        },
        "info": {
            "format": "{{data.text}}",
        },
    }

    def __init__(self):
        # Bytes accumulated for the sub-exchange currently being clocked.
        # Each entry is (start_time, end_time, mosi_byte, miso_byte).
        self._samples = []
        self._active = False

        # Last header seen per direction, used to walk the data phase.
        # {"numPackets": int, "dataLength": int, "protocol": int}
        self._mosi_hdr = None
        self._miso_hdr = None

    # ------------------------------------------------------------------
    # Frame intake
    # ------------------------------------------------------------------
    def decode(self, frame: AnalyzerFrame):
        if frame.type == "enable":
            # New chip-select assertion => new sub-exchange.
            out = self._flush()
            self._samples = []
            self._active = True
            return out

        if frame.type == "result":
            mosi = self._word(frame.data.get("mosi"))
            miso = self._word(frame.data.get("miso"))
            if not self._active:
                # No enable line configured, or a result before the first
                # enable: start a sub-exchange implicitly.
                self._active = True
                self._samples = []
            self._samples.append((frame.start_time, frame.end_time, mosi, miso))
            return None

        if frame.type == "disable":
            out = self._flush()
            self._active = False
            return out

        return None

    @staticmethod
    def _word(value):
        """Reduce a per-word payload (bytes/int/None) to a single byte value."""
        if value is None:
            return 0
        if isinstance(value, (bytes, bytearray)):
            if len(value) == 0:
                return 0
            # 8 bits per transfer is expected; if wider, keep the low byte.
            return value[-1]
        return int(value) & 0xFF

    # ------------------------------------------------------------------
    # Sub-exchange decoding
    # ------------------------------------------------------------------
    def _flush(self):
        """Decode the accumulated sub-exchange and return AnalyzerFrames."""
        if not self._samples:
            return None

        samples = self._samples
        self._samples = []
        n = len(samples)

        is_mosi = self.direction == "MOSI (SBC->RRF)"
        buf = bytes(s[2 if is_mosi else 3] for s in samples)

        # Each decoder returns (i0, i1, ftype, data) byte-range annotations, which
        # map directly onto the sample times of those bytes.
        frames = []
        for (a, b, ftype, data) in self._decode_stream(is_mosi, buf, n):
            frames.append(AnalyzerFrame(ftype, samples[a][0], samples[b - 1][1], data))
        return frames if frames else None

    def _decode_stream(self, is_mosi, buf, n):
        # Classify the sub-exchange by size + a positive format-code test.
        # A transfer header's first uint32 is 0x0007nn5F, whose low byte is the
        # format code (0x5F/0x60). No response code and no packet request aliases
        # that, so the format code positively identifies a header.
        first = buf[0] if n else None

        if n in (HEADER_SIZE_V4, HEADER_SIZE_LEGACY) and first in (
            FORMAT_CODE, FORMAT_CODE_STANDALONE, INVALID_FORMAT_CODE,
        ):
            return self._decode_header(is_mosi, buf, n)

        if n == RESPONSE_SIZE:
            return self._decode_response(buf)

        # Anything else is a data phase full of packet headers.
        return self._decode_data(is_mosi, buf, n)

    # ------------------------------------------------------------------
    def _decode_header(self, is_mosi, buf, n):
        fmt = buf[0]
        num_packets = buf[1]
        protocol = _u16(buf, 2)
        seq = _u16(buf, 4)
        data_length = _u16(buf, 6)

        legacy = n == HEADER_SIZE_LEGACY
        if legacy:
            crc_data = "0x%04X" % _u16(buf, 8)
            crc_header = "0x%04X" % _u16(buf, 10)
        else:
            crc_data = "0x%08X" % _u32(buf, 8)
            crc_header = "0x%08X" % _u32(buf, 12)

        fmt_name = {
            FORMAT_CODE: "0x5F(SBC)",
            FORMAT_CODE_STANDALONE: "0x60(standalone)",
            INVALID_FORMAT_CODE: "0xC9(invalid)",
        }.get(fmt, "0x%02X" % fmt)

        # Remember header framing for the following data phase.
        info = {"numPackets": num_packets, "dataLength": data_length, "protocol": protocol}
        if is_mosi:
            self._mosi_hdr = info
        else:
            self._miso_hdr = info

        return [(0, n, "header", {
            "format": fmt_name,
            "numPackets": num_packets,
            "protocol": protocol,
            "seq": seq,
            "dataLength": data_length,
            "crcData": crc_data,
            "crcHeader": crc_header,
        })]

    def _decode_response(self, buf):
        value = _u32(buf, 0)
        name = RESPONSE_NAMES.get(value, "Unknown 0x%08X" % value)
        return [(0, RESPONSE_SIZE, "response", {
            "name": name,
            "value": "0x%08X" % value,
        })]

    def _decode_data(self, is_mosi, buf, n):
        hdr = self._mosi_hdr if is_mosi else self._miso_hdr
        if hdr is not None:
            max_packets = hdr["numPackets"]
            data_length = min(hdr["dataLength"], n)
        else:
            # Capture started mid-stream: walk until the buffer is consumed.
            max_packets = 0xFFFF
            data_length = n

        ann = []
        request_map = SBC_REQUESTS if is_mosi else FIRMWARE_REQUESTS
        offset = 0
        count = 0
        while count < max_packets and offset + PACKET_HEADER_SIZE <= data_length:
            request = _u16(buf, offset)
            pkt_id = _u16(buf, offset + 2)
            length = _u16(buf, offset + 4)
            resend = _u16(buf, offset + 6)

            req_name = request_map.get(request, "Req%d" % request)
            resend_txt = "" if resend == 0 else " resend=%d" % resend

            ann.append((offset, offset + PACKET_HEADER_SIZE, "packet", {
                "request": req_name,
                "id": pkt_id,
                "length": length,
                "resend": resend_txt,
            }))

            # Decode the request-specific payload header that follows, if known.
            parser = PAYLOAD_PARSERS.get((is_mosi, request))
            payload_off = offset + PACKET_HEADER_SIZE
            if parser is not None and length > 0:
                try:
                    parsed = parser(buf, payload_off)
                except (IndexError, struct.error):
                    parsed = []
                if isinstance(parsed, tuple):
                    parsed = [parsed]

                # Sub-structs are laid out back to back; stop at the first one
                # that would run past the packet payload or the captured data.
                sub_off = payload_off
                for (size, name, detail) in parsed:
                    if not 0 < size <= payload_off + length - sub_off:
                        break
                    if sub_off + size > data_length:
                        break
                    ann.append((sub_off, sub_off + size, "subheader", {
                        "name": name,
                        "detail": detail,
                    }))
                    sub_off += size

            offset += PACKET_HEADER_SIZE + _pad4(length)
            count += 1

        if not ann:
            # Unclassifiable / short exchange - still show something useful.
            ann.append((0, n, "info", {
                "text": "data %d bytes (no packet headers decoded)" % n,
            }))
        return ann
