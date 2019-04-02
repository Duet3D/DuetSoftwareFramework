namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request indices for SPI transfers from the RepRapFirmware controller to the Linux board
    /// </summary>
    public enum Request : ushort
    {
        /// <summary>
        /// Request retransmission of the given packet.
        /// This is always guaranteed to work in case RRF does not have not enough resources are available to process the packet
        /// </summary>
        ResendPacket = 0,

        /// <summary>
        /// Response to the state request
        /// </summary>
        ReportState = 1,

        /// <summary>
        /// Response to an object model request
        /// </summary>
        ObjectModel = 2,
        
        /// <summary>
        /// Response to a G/M/T-code
        /// </summary>
        CodeReply = 3,

        /// <summary>
        /// Request execution of a macro file
        /// </summary>
        ExecuteMacro = 4,

        /// <summary>
        /// Request all files of the code channel to be closed
        /// </summary>
        AbortFile = 5,
        
        /// <summary>
        /// Stack has been changed
        /// </summary>
        StackEvent = 6,
        
        /// <summary>
        /// Print has been paused
        /// </summary>
        PrintPaused = 7,
        
        /// <summary>
        /// Response to a heightmap request
        /// </summary>
        HeightMap = 8,

        /// <summary>
        /// Ressource locked
        /// </summary>
        Locked = 9
    }
}