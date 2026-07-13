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
    /// Notify the transfer loop that there is a reason to initiate a full transfer, e.g. because new data
    /// has been queued for transmission. Adapters that block while idle use this to wake up promptly
    /// </summary>
    void RequestTransfer();

    /// <summary>
    /// Decide whether a full transfer should be started, blocking while idle until there is a reason to.
    /// The transfer loop must call this in a loop after staging outgoing data, e.g.
    /// <c>do { StageOutgoingData(); } while (!WaitForTransferReason());</c>, and perform a transfer only
    /// once it returns true. Re-staging data before each decision avoids both a leading and a trailing empty
    /// transfer. Adapters that do not gate transfers while idle always return true
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if a transfer should be started now, false if the caller should re-stage data and retry</returns>
    bool WaitForTransferReason(CancellationToken cancellationToken = default);

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
    /// Read a forwarded CAN message (single fragment) from an expansion board
    /// </summary>
    /// <param name="txToken">Token mapping the response back to its request</param>
    /// <param name="msgType">Type of the received CAN message</param>
    /// <param name="srcAddress">Source address of the replying board</param>
    /// <param name="flags">Flags of the CAN message</param>
    /// <param name="status">Status of the CAN message</param>
    /// <param name="payload">CAN payload of this fragment</param>
    void ReadCanResponse(out ushort txToken, out CanMessageType msgType, out byte srcAddress, out byte flags, out CanStatus status, out byte[] payload);

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

    /// <summary>
    /// Enable or disable the CAN bus on the DuetCANMaster board.
    /// </summary>
    /// <param name="enable">True to enable the CAN bus, false to disable it</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteEnableCan(bool enable);

    /// <summary>
    /// Send a CAN message to an expansion board
    /// </summary>
    /// <param name="txToken">Token used to map the response back to the request</param>
    /// <param name="msgType">CanMessageType to place in the CAN id</param>
    /// <param name="replyType">Expected reply type (0xFFFF if no reply is expected)</param>
    /// <param name="dstAddress">CAN destination: 0..126, or 127 for broadcast</param>
    /// <param name="isResponse">Whether this message is a response</param>
    /// <param name="payload">CAN payload (0..64 bytes)</param>
    /// <returns>Whether the request could be written</returns>
    bool WriteCanMessage(ushort txToken, ushort msgType, ushort replyType, byte dstAddress, bool isResponse, ReadOnlySpan<byte> payload);
}
