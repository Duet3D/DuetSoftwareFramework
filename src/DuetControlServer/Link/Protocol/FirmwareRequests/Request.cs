namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// Request indices for SPI transfers from the RepRapFirmware controller to the SBC
/// </summary>
public enum Request : ushort
{
    /// <summary>
    /// Request retransmission of the given packet.
    /// This is always guaranteed to work in case RRF does not have not enough resources are available to process the packet
    /// </summary>
    ResendPacket = 0,

    /// <summary>
    /// Update about the available code buffer size
    /// </summary>
    /// <seealso cref="CodeBufferUpdateHeader"/>
    CodeBufferUpdate = 2,

    /// <summary>
    /// Message from the firmware
    /// </summary>
    /// <seealso cref="Shared.MessageHeader"/>
    Message = 3,

    /// <summary>
    /// The current master clock time
    /// </summary>
    /// <seealso cref="MasterClockHeader"/>
    MasterClock = 4,

    /// <summary>
    /// Forwarded CAN message from expansion boards
    /// </summary>
    /// <seealso cref="CanResponseHeader"/>
    CANResponse = 5,

    /// <summary>
    /// Drive(s) that have stopped
    /// </summary>
    /// <seealso cref="MotionStoppedHeader"/>
    MotionStopped = 6,
}
