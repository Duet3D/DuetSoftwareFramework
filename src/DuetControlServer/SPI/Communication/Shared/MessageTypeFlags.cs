using DuetAPI;
using System;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Message type flags describing a code reply. This is equal to MessageType in RepRapFirmware.
    /// Make sure to keep the destinations in sync with the <see cref="CodeChannel"/> entries
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
        /// A message that is to be sent to the panel
        /// </summary>
        AuxMessage = 0x10,

        /// <summary>
        /// A message that is to be sent to the code queue channel
        /// </summary>
        TriggerMessage = 0x20,

        /// <summary>
        /// A message that is to be sent to the code queue channel
        /// </summary>
        QueueMessage = 0x40,

        /// <summary>
        /// A message that is to be sent to the panel
        /// </summary>
        LcdMessage = 0x80,

        /// <summary>
        /// A message that is to be sent to the SPI master
        /// </summary>
        SbcMessage = 0x100,

        /// <summary>
        /// A message that is to be sent to a daemon processor
        /// </summary>
        DaemonMessage = 0x200,

        /// <summary>
        /// A message that is to be sent to the second UART port
        /// </summary>
        Aux2Message = 0x400,

        /// <summary>
        /// A message that is to be sent to an auto-pause processor
        /// </summary>
        AutoPauseMessage = 0x800,
        #endregion

        #region Special destinations (byte 3)
        /// <summary>
        /// A message that is to be sent to USB in blocking mode
        /// </summary>
        BlockingUsbMessage = 0x10000,

        /// <summary>
        /// A message that is to be sent to LCD in immediate mode
        /// </summary>
        ImmediateLcdMessage = 0x20000,
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

        // 0x4000000 is now unused

        /// <summary>
        /// Do not encapsulate this message
        /// </summary>
        RawMessageFlag = 0x8000000, // Do not encapsulate this message

        /// <summary>
        /// This message comes from a binary G-Code buffer
        /// </summary>
        BinaryCodeReplyFlag = 0x10000000,   // This message comes from a binary G-Code buffer

        /// <summary>
        /// There is more to come; the message has been truncated
        /// </summary>
        PushFlag = 0x20000000,  // There is more to come; the message has been truncated

        /// <summary>
        /// Log level consists of two bits. This is the low bit
        /// </summary>
        LogMessageLowBit = 0x40000000,

        /// <summary>
        /// Log level consists of two bits. This is the high bit
        /// </summary>
        LogMessageHighBit = 0x80000000,
        #endregion

        #region Common combination
        /// <summary>
        /// A message that is to be sent to the web, Telnet, USB and panel
        /// </summary>
        GenericMessage = UsbMessage | AuxMessage | HttpMessage | TelnetMessage,

        /// <summary>
        /// Log level "off (3): do not log this message
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
}
