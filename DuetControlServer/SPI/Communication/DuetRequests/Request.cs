namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Request indices for SPI transfers from the RepRapFirmware controller to the Linux board
    /// </summary>
    public enum Request : ushort
    {
        /// <summary>
        /// Response to an object model request
        /// </summary>
        ObjectModel = 0,
        
        /// <summary>
        /// Response to a G/M/T-code
        /// </summary>
        CodeReply = 1,
        
        /// <summary>
        /// Request the execution of a macro file
        /// </summary>
        MacroRequest = 2,
        
        /// <summary>
        /// Stack has been pushed
        /// </summary>
        StackPushed = 3,
        
        /// <summary>
        /// Stack has been popped
        /// </summary>
        StackPopped = 4,
        
        /// <summary>
        /// Print has been paused
        /// </summary>
        PrintPaused = 5,
        
        /// <summary>
        /// Response to a heightmap request
        /// </summary>
        Heightmap = 6,
        
        /// <summary>
        /// Resend a received packet (most likely because the checksum did not match)
        /// </summary>
        ResendPacket = 255
    }
}