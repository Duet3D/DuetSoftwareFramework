namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// Reasons why a CAN message has been received
/// </summary>
public enum CANStatus : byte
{
    /// <summary>
    /// Reply received without error
    /// </summary>
    Ok = 0,

    /// <summary>
    /// No reply received within the timeout period
    /// </summary>
    Timeout = 1,


    /// <summary>
    /// Transmit failed or the request was malformed
    /// </summary>
    BusError = 2,

    /// <summary>
    /// The HAT could not allocate a CAN buffer for the request
    /// </summary>
    NoBuffer = 3,

    /// <summary>
    /// Reply larger than the SBC could handle
    /// </summary>
    Overflow = 4,
}
