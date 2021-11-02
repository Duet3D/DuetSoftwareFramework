namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
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
        /// Response to an object model request
        /// </summary>
        /// <seealso cref="Shared.StringHeader"/>
        ObjectModel = 1,

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
        /// Request execution of a macro file
        /// </summary>
        /// <seealso cref="ExecuteMacroHeader"/>
        ExecuteMacro = 4,

        /// <summary>
        /// Request all files of the code channel to be closed
        /// </summary>
        /// <seealso cref="AbortFileHeader"/>
        AbortFile = 5,
        
        /// <summary>
        /// Stack has been changed. This is no longer used
        /// </summary>
        StackEvent_Obsolete = 6,

        /// <summary>
        /// Print has been paused
        /// </summary>
        /// <seealso cref="PrintPausedHeader"/>
        PrintPaused = 7,
        
        /// <summary>
        /// Response to a heightmap request. This is no longer used
        /// </summary>
        /// <seealso cref="Shared.HeightMapHeader"/>
        HeightMap_Obsolete = 8,

        /// <summary>
        /// Ressource locked
        /// </summary>
        /// <seealso cref="Shared.CodeChannelHeader"/>
        Locked = 9,

        /// <summary>
        /// Request another chunk of a file
        /// </summary>
        /// <seealso cref="FileChunkHeader"/>
        FileChunk = 10,

        /// <summary>
        /// Response to an expression evaluation request
        /// </summary>
        /// <seealso cref="EvaluationResultHeader"/>
        EvaluationResult = 11,

        /// <summary>
        /// Perform a G/M/T-code from a RepRapFirmware code input
        /// </summary>
        /// <seealso cref="DoCodeHeader"/>
        DoCode = 12,

        /// <summary>
        /// Firmware is waiting for a blocking message to be acknowledged
        /// </summary>
        /// <seealso cref="Shared.CodeChannelHeader"/>
        WaitForAcknowledgement = 13,

        /// <summary>
        /// Last file closed successfully
        /// </summary>
        MacroFileClosed = 14,

        /// <summary>
        /// Last message successfully acknowledged
        /// </summary>
        MessageAcknowledged = 15,

        /// <summary>
        /// Response to a variable set request
        /// </summary>
        /// <seealso cref="EvaluationResultHeader"/>
        VariableResult = 16,

        /// <summary>
        /// Check if a file exists
        /// </summary>
        CheckFileExists = 17,

        /// <summary>
        /// Delete a file or directory
        /// </summary>
        DeleteFileOrDirectory = 18,

        /// <summary>
        /// Open a file on the SBC
        /// </summary>
        OpenFile = 19,

        /// <summary>
        /// Read from a file
        /// </summary>
        ReadFile = 20,

        /// <summary>
        /// Write to a file
        /// </summary>
        WriteFile = 21,

        /// <summary>
        /// Seek in a file
        /// </summary>
        SeekFile = 22,

        /// <summary>
        /// Truncate a file
        /// </summary>
        TruncateFile = 23,

        /// <summary>
        /// Close a file again
        /// </summary>
        CloseFile = 24
    }
}