using System;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Request indices for SPI transfers from the SBC to the RepRapFirmware controller
/// </summary>
public enum Request : ushort
{
    /// <summary>
    /// Perform an immediate emergency stop
    /// </summary>
    EmergencyStop = 0,

    /// <summary>
    /// Reset the controller
    /// </summary>
    Reset = 1,

    /// <summary>
    /// Configure the CAN bus interface
    /// </summary>
    /// <seealso cref="ConfigCANHeader"/>
    ConfigCAN = 2,

    /// <summary>
    /// Enable the CAN bus interface
    /// </summary>
    EnableCAN = 3,

    /// <summary>
    /// Disable the CAN bus interface
    /// </summary>
    DisableCAN = 4,

    /// <summary>
    /// Schedule a move on the controller
    /// </summary>
    /// <seealso cref="ScheduleMoveHeader"/>
    ScheduleMove = 5,

    /// <summary>
    /// Send a CAN message to the controller
    /// </summary>
    /// <seealso cref="SendCANMessageHeader"/>
    SendCANMessage = 6,

    /// <summary>
    /// Write another chunk of the IAP binary to the designated Flash area
    /// </summary>
    /// <remarks>
    /// There is no discrete header for this request but be aware that only multiples
    /// of IFLASH_PAGE_SIZE must be transmitted (except for the last sector)
    /// </remarks>
    WriteIap = 7,

    /// <summary>
    /// Launch the IAP binary
    /// </summary>
    StartIap = 8,

    /// <summary>
    /// Send an arbitrary RepRapFirmware message
    /// </summary>
    /// <seealso cref="Shared.MessageHeader"/>
    Message = 9,
}
