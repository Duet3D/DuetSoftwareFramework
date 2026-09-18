namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// What the controller made of a CAN message the SBC asked it to send.
/// </summary>
/// <remarks>
/// A wire value, and it goes no further than <see cref="LinkInterface.CompleteCanMessageSent"/>. Above
/// the link layer what became of a request is a <see cref="Shared.CodeResult"/>, as it is in
/// RepRapFirmware, and the line between the two is whether the frame reached the CAN peripheral: a value that says
/// it did converts to a result code, and a value that says it did not fails the request instead, since
/// a G-code result can only describe an answer about the machine.
/// </remarks>
public enum CanStatus : byte
{
    /// <summary>
    /// Reply received without error
    /// </summary>
    Ok = 0,

    /// <summary>
    /// The frame reached the bus and the board did not reply in time
    /// </summary>
    ResponseTimeout = 1,

    /// <summary>
    /// Refused before transmission: the bus is disabled, or the request was malformed
    /// </summary>
    BusError = 2,

    /// <summary>
    /// The controller had no buffer for the request or for its reply
    /// </summary>
    NoBuffer = 3,

    /// <summary>
    /// Reply larger than the SBC could handle
    /// </summary>
    Overflow = 4,

    /// <summary>
    /// The frame was handed to the CAN peripheral and no node on the bus ever acknowledged it, so it
    /// was never transmitted
    /// </summary>
    DispatchTimeout = 5,
}
