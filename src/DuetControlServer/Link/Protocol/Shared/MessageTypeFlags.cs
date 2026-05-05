using DuetAPI;
using System;

namespace DuetControlServer.Link.Protocol.Shared;

/// <summary>
/// Supported message destinations. This is now a bitmap. Note that this type is used by RepRapFirmware as well.
/// Make sure to keep the destinations in sync with the <see cref="CodeChannel"/> entries.
/// </summary>
[Flags]
public enum MessageTypeFlags : uint
{
    #region Destinations (bytes 1-2). Keep the following in sync with the order of GCodeBuffers in the GCodes class
    /// <summary>
    /// A message that is to be sent to the web (HTTP)
    /// </summary>
    HttpMessage = 0x01,

    /// <summary>
    /// A message that is to be sent to a Telnet client
    /// </summary>
    TelnetMessage = 0x02,

    /// <summary>
    /// A message that is to be sent to a file processor
    /// </summary>
    FileMessage = 0x04,

    /// <summary>
    /// A message that is to be sent in non-blocking mode to the host via USB
    /// </summary>
    UsbMessage = 0x08,

    /// <summary>
    /// A message that is to be sent to an auxiliary device (PanelDue)
    /// </summary>
    AuxMessage = 0x10,

    /// <summary>
    /// A message that is to be sent to a trigger processor
    /// </summary>
    TriggerMessage = 0x20,

    /// <summary>
    /// A message that is to be sent to the code queue channel
    /// </summary>
    CodeQueueMessage = 0x40,

    /// <summary>
    /// A message that is to be sent to the panel
    /// </summary>
    LcdMessage = 0x80,

    /// <summary>
    /// A message that is to be sent to the SBC
    /// </summary>
    SbcMessage = 0x100,

    /// <summary>
    /// A message that is sent to the daemon processor
    /// </summary>
    DaemonMessage = 0x200,

    /// <summary>
    /// A message that is to be sent to the second aux device
    /// </summary>
    Aux2Message = 0x400,

    /// <summary>
    /// A message that is to be sent to an auto-pause processor
    /// </summary>
    AutoPauseMessage = 0x800,

    /// <summary>
    /// A message that is to be sent to the second file processor
    /// </summary>
    File2Message = 0x1000,

    /// <summary>
    /// A message that is to be sent to the second code queue channel
    /// </summary>
    Queue2Message = 0x2000,

    /// <summary>
    /// A message that is to be sent to the second USB channel
    /// </summary>
    Usb2Message = 0x4000,

    /// <summary>
    /// A message that is to be published by the MQTT client
    /// </summary>
    MqttMessage = 0x8000,
    #endregion

    #region Special destinations (byte 3)
    /// <summary>
    /// A message that is to be sent to USB in blocking mode
    /// </summary>
    BlockingUsbMessage = 0x10000,

    /// <summary>
    /// A message that is to be sent to LCD in immediate mode
    /// </summary>
    ImmediateAuxMessage = 0x20000,
    #endregion

    #region Special indicators (byte 4)
    /// <summary>
    /// This is an error message
    /// </summary>
    ErrorMessageFlag = 0x1000000,

    /// <summary>
    /// This is a warning message
    /// </summary>
    WarningMessageFlag = 0x2000000,

    /// <summary>
    /// Do not encapsulate this message
    /// </summary>
    RawMessageFlag = 0x8000000,

    /// <summary>
    /// This message comes from a binary G-Code buffer
    /// </summary>
    BinaryCodeReplyFlag = 0x10000000,

    /// <summary>
    /// There is more to come; the message has been truncated
    /// </summary>
    PushFlag = 0x20000000,

    /// <summary>
    /// Log level consists of two bits, this is the low bit
    /// </summary>
    LogMessageLowBit = 0x40000000,

    /// <summary>
    /// Log level consists of two bits, this is the high bit
    /// </summary>
    LogMessageHighBit = 0x80000000,
    #endregion

    #region Common combinations
    /// <summary>
    /// A message that is going nowhere
    /// </summary>
    NoDestinationMessage = 0,

    /// <summary>
    /// A message that is to be sent to the web, Telnet, USB and panel
    /// </summary>
    GenericMessage = UsbMessage | AuxMessage | HttpMessage | TelnetMessage,

    /// <summary>
    /// Log level "off" (3): do not log this message
    /// </summary>
    LogOff = LogMessageLowBit | LogMessageHighBit,

    /// <summary>
    /// Log level "warn" (2): all messages of type Error and Warning are logged
    /// </summary>
    LogWarn = LogMessageHighBit,

    /// <summary>
    /// Log level "info" (1): all messages of level "warn" plus info messages
    /// </summary>
    LogInfo = LogMessageLowBit,

    /// <summary>
    /// A GenericMessage that is also logged
    /// </summary>
    LoggedGenericMessage = GenericMessage | LogWarn,
    #endregion
}
