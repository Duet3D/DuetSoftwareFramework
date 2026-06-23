using System;
using System.IO;
using System.Threading;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Adapter;

/// <summary>
/// Interface for hardware link adapters
/// </summary>
public interface ILinkAdapter
{
    /// <summary>
    /// Attempt to connect to the firmware
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    void Connect(CancellationToken cancellationToken = default);

    /// <summary>
    /// Currently-used protocol version
    /// </summary>
    int ProtocolVersion { get; }

    /// <summary>
    /// Perform a full data transfer synchronously
    /// </summary>
    /// <param name="connecting">Whether this an initial connection is being established</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    void PerformFullTransfer(bool connecting = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the maximum time between two full transfers
    /// </summary>
    /// <returns>Time in ms</returns>
    double GetMaxFullTransferDelay();

    /// <summary>
    /// Check if the controller has been reset
    /// </summary>
    /// <returns>Whether the controller has been reset</returns>
    bool HadReset();

    /// <summary>
    /// Returns the number of packets to read
    /// </summary>
    int PacketsToRead { get; }

    /// <summary>
    /// Read the next packet
    /// </summary>
    /// <returns>The next packet or null if none is available</returns>
    PacketHeader? ReadNextPacket();

    /// <summary>
    /// Read a code buffer update
    /// </summary>
    /// <param name="bufferSpace">Buffer space</param>
    void ReadCodeBufferUpdate(out ushort bufferSpace);

    /// <summary>
    /// Read an incoming message
    /// </summary>
    /// <param name="messageType">Message type flags of the reply</param>
    /// <param name="reply">Code reply</param>
    void ReadMessage(out MessageTypeFlags messageType, out string reply);

    /// <summary>
    /// Read a code channel
    /// </summary>
    /// <param name="channel">Code channel that has acquired the lock</param>
    /// <returns>Asynchronous task</returns>
    void ReadCodeChannel(out CodeChannel channel);

    /// <summary>
    /// Write the last packet + content for diagnostic purposes
    /// </summary>
    void DumpMalformedPacket();

    /// <summary>
    /// Resend a packet back to the firmware
    /// </summary>
    /// <param name="packet">Packet holding the resend request</param>
    /// <param name="sbcRequest">Content of the packet to resend</param>
    void ResendPacket(PacketHeader packet, out Protocol.SbcRequests.Request sbcRequest);

    /// <summary>
    /// Write another segment of the IAP binary
    /// </summary>
    /// <param name="stream">IAP binary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether another segment could be written</returns>
    bool WriteIapSegment(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Instruct the firmware to start the IAP binary
    /// </summary>
    /// <param name="firmwareLength">Length of the firmware binary in bytes (used by USB IAP for end-of-transfer detection; ignored by SPI)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    void StartIap(uint firmwareLength, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flash another segment of the firmware via the IAP binary
    /// </summary>
    /// <param name="stream">Stream of the firmware binary</param>
    /// <returns>Whether another segment could be sent</returns>
    bool FlashFirmwareSegment(Stream stream);

    /// <summary>
    /// Send the CRC16 checksum of the firmware binary to the IAP program and verify the written data
    /// </summary>
    /// <param name="firmwareLength">Length of the written firmware in bytes</param>
    /// <param name="crc16">CRC16 checksum of the firmware</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    bool VerifyFirmwareChecksum(long firmwareLength, ushort crc16);

    /// <summary>
    /// Wait for the IAP program to reset the controller
    /// </summary>
    void WaitForIapReset();

    /// <summary>
    /// Request an emergency stop
    /// </summary>
    /// <returns>True if the packet could be written</returns>
    bool WriteEmergencyStop();

    /// <summary>
    /// Request a firmware reset
    /// </summary>
    /// <returns>True if the packet could be written</returns>
    bool WriteReset();

    /// <summary>
    /// Write a message
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="message">Message content</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    bool WriteMessage(MessageTypeFlags flags, string message);
}
