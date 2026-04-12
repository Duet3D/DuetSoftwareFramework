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
    /// Read the result of a <see cref="Protocol.SbcRequests.Request.GetObjectModel"/> request
    /// </summary>
    /// <param name="json">JSON data</param>
    void ReadObjectModel(out ReadOnlySpan<byte> json);

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
    /// Read the content of a <see cref="ExecuteMacroHeader"/> packet
    /// </summary>
    /// <param name="channel">Channel requesting a macro file</param>
    /// <param name="isSystemMacro">Indicates if this code is not bound to a code being executed (e.g. when a trigger macro is requested)</param>
    /// <param name="filename">Filename of the requested macro</param>
    void ReadMacroRequest(out CodeChannel channel, out bool isSystemMacro, out string filename);

    /// <summary>
    /// Read the content of an <see cref="AbortFileHeader"/> packet
    /// </summary>
    /// <param name="channel">Code channel where all files are supposed to be aborted</param>
    /// <param name="abortAll">Whether all files are supposed to be aborted</param>
    void ReadAbortFile(out CodeChannel channel, out bool abortAll);

    /// <summary>
    /// Read the content of a <see cref="PrintPausedHeader"/> packet
    /// </summary>
    /// <param name="filePosition">Position where the print has been paused</param>
    /// <param name="filePosition2">Secondary file position where the print has been paused</param>
    /// <param name="reason">Reason why the print has been paused</param>
    void ReadPrintPaused(out uint filePosition, out uint filePosition2, out PrintPausedReason reason);

    /// <summary>
    /// Read a code channel
    /// </summary>
    /// <param name="channel">Code channel that has acquired the lock</param>
    /// <returns>Asynchronous task</returns>
    void ReadCodeChannel(out CodeChannel channel);

    /// <summary>
    /// Read a chunk of a <see cref="Request.FileChunk"/> packet
    /// </summary>
    /// <param name="filename">Filename</param>
    /// <param name="offset">File offset</param>
    /// <param name="maxLength">Maximum chunk size</param>
    void ReadFileChunkRequest(out string filename, out uint offset, out int maxLength);

    /// <summary>
    /// Read the result of an expression evaluation request
    /// </summary>
    /// <param name="channel">Channel where the evaluation was performed</param>
    /// <param name="expression">Evaluated expression</param>
    /// <param name="result">Result</param>
    void ReadEvaluationResult(out CodeChannel? channel, out string expression, out object? result);

    /// <summary>
    /// Read a code request
    /// </summary>
    /// <param name="channel">Channel to execute this code on</param>
    /// <param name="code">Code to execute</param>
    void ReadDoCode(out CodeChannel channel, out string code);

    /// <summary>
    /// Read a request to check if a file exists
    /// </summary>
    /// <param name="filename">Name of the file</param>
    void ReadCheckFileExists(out string filename);

    /// <summary>
    /// Read a request to delete a file or directory
    /// </summary>
    /// <param name="filename">Name of the file</param>
    void ReadDeleteFileOrDirectory(out string filename);

    /// <summary>
    /// Read an open file request
    /// </summary>
    /// <param name="filename">Filename to open</param>
    /// <param name="forWriting">Whether the file is supposed to be written to</param>
    /// <param name="append">Whether data is supposed to be appended in write mode</param>
    /// <param name="preAllocSize">How many bytes to allocate if the file is created or overwritten</param>
    void ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize);

    /// <summary>
    /// Read a request to seek in a file
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="offset">New file position</param>
    void ReadSeekFile(out uint handle, out long offset);

    /// <summary>
    /// Read a request to truncate a file
    /// </summary>
    /// <param name="handle">File handle</param>
    void ReadTruncateFile(out uint handle);

    /// <summary>
    /// Read a request to read data from a file
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="maxLength">Maximum data length</param>
    void ReadFileRequest(out uint handle, out int maxLength);

    /// <summary>
    /// Read a request to write data to a file
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="data">Data to write</param>
    void ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data);

    /// <summary>
    /// Read a request to close a file
    /// </summary>
    /// <param name="handle">File handle</param>
    void ReadCloseFile(out uint handle);

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
    /// Request a code to be executed
    /// </summary>
    /// <param name="code">Code to send</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteCode(Commands.Code code);

    /// <summary>
    /// Request the key of an object module of a specific module
    /// </summary>
    /// <param name="key">Object model key to query</param>
    /// <param name="flags">Object model flags to query</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteGetObjectModel(string key, string flags);

    /// <summary>
    /// Notify the firmware that a file print has started
    /// </summary>
    /// <param name="info">Information about the file being printed</param>
    /// <returns>True if the packet could be written</returns>
    bool WritePrintFileInfo(GCodeFileInfo info);

    /// <summary>
    /// Notify that a file print has been stopped
    /// </summary>
    /// <param name="reason">Reason why the print has been stopped</param>
    /// <returns>True if the packet could be written</returns>
    bool WritePrintStopped(PrintStoppedReason reason);

    /// <summary>
    /// Notify the firmware about a completed macro file. This function is only used for macro files that the firmware requested
    /// </summary>
    /// <param name="channel">Code channel of the finished macro</param>
    /// <param name="error">Whether an error occurred</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteMacroCompleted(CodeChannel channel, bool error);

    /// <summary>
    /// Request the movement systems to be locked and wait for standstill
    /// </summary>
    /// <param name="channel">Code channel that requires the lock</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel channel);

    /// <summary>
    /// Release all acquired locks again
    /// </summary>
    /// <param name="channel">Code channel that releases the locks</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteUnlock(CodeChannel channel);

    /// <summary>
    /// Write another chunk of the file being requested
    /// </summary>
    /// <param name="data">File chunk data</param>
    /// <param name="fileLength">Total length of the file in bytes</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    bool WriteFileChunk(Span<byte> data, long fileLength);

    /// <summary>
    /// Write a request for an expression evaluation
    /// </summary>
    /// <param name="channel">Where to evaluate the expression</param>
    /// <param name="expression">Expression to evaluate</param>
    /// <returns>Whether the evaluation request has been written successfully</returns>
    bool WriteEvaluateExpression(CodeChannel channel, string expression);

    /// <summary>
    /// Write a message
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="message">Message content</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    bool WriteMessage(MessageTypeFlags flags, string message);

    /// <summary>
    /// Notify RepRapFirmware that a macro file could be started
    /// </summary>
    /// <param name="channel">Code channel that requires the lock</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteMacroStarted(CodeChannel channel);

    /// <summary>
    /// Called when a code channel is supposed to be invalidated (e.g. via abort keyword)
    /// </summary>
    /// <param name="channel">Code channel that requires the lock</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteInvalidateChannel(CodeChannel channel);

    /// <summary>
    /// Set a global or local variable
    /// </summary>
    /// <param name="channel">G-code channel</param>
    /// <param name="createVariable">Whether the variable should be created or updated</param>
    /// <param name="varName">Name of the variable including global or var prefix</param>
    /// <param name="expression">New value of the variable</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteSetVariable(CodeChannel channel, bool createVariable, string varName, string expression);

    /// <summary>
    /// Delete a local variable at the end of the current code block
    /// </summary>
    /// <param name="channel">G-code channel</param>
    /// <param name="varName">Name of the variable excluding var prefix</param>
    /// <returns>True if the packet could be written</returns>
    bool WriteDeleteLocalVariable(CodeChannel channel, string varName);

    /// <summary>
    /// Send back whether a file exists or not
    /// </summary>
    /// <param name="exists">Whether the file exists</param>
    /// <returns>If the packet could be written</returns>
    bool WriteCheckFileExistsResult(bool exists);

    /// <summary>
    /// Send back whether a file or directory could be deleted
    /// </summary>
    /// <param name="success">Whether the file operation was successful</param>
    /// <returns>If the packet could be written</returns>
    bool WriteFileDeleteResult(bool success);

    /// <summary>
    /// Write the new file handle and file length of the file that has just been opened
    /// </summary>
    /// <param name="fileHandle">New file handle or noFileHandle if the file could not be opened</param>
    /// <param name="length">Length of the file</param>
    /// <returns>If the packet could be written</returns>
    bool WriteOpenFileResult(uint fileHandle, long length);

    /// <summary>
    /// Write requested read data from a file
    /// </summary>
    /// <param name="data">File data</param>
    /// <param name="bytesRead">Number of bytes read or negative on error</param>
    /// <returns>If the packet could be written</returns>
    bool WriteFileReadResult(Span<byte> data, int bytesRead);

    /// <summary>
    /// Tell RRF if the last file block could be written
    /// </summary>
    /// <param name="success">If the file data could be written</param>
    /// <returns>If the packet could be written</returns>
    bool WriteFileWriteResult(bool success);

    /// <summary>
    /// Tell RRF if the seek operation was successful
    /// </summary>
    /// <param name="success">If the seek operation succeeded</param>
    /// <returns>If the packet could be written</returns>
    bool WriteFileSeekResult(bool success);

    /// <summary>
    /// Tell RRF if the seek operation was successful
    /// </summary>
    /// <param name="success">If the seek operation succeeded</param>
    /// <returns>If the packet could be written</returns>
    bool WriteFileTruncateResult(bool success);

    /// <summary>
    /// Write the last code result for a specific code channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="result">Last code result</param>
    /// <returns>If the packet could be written</returns>
    bool WriteSetLastCodeResult(CodeChannel channel, CodeResult result);

    /// <summary>
    /// Notify RRF that an object model key has changed
    /// </summary>
    /// <param name="key">Key that has changed</param>
    /// <returns>If the packet could be written</returns>
    bool WriteObjectModelKeyChanged(string key);
}
