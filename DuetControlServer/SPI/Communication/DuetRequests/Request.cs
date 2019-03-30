namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Request indices for SPI transfers from the RepRapFirmware controller to the Linux board
    /// </summary>
    public enum Request : ushort
    {
        /// <summary>
        /// Response to the state request
        /// </summary>
        ReportState = 0,

        /// <summary>
        /// Response to an object model request
        /// </summary>
        ObjectModel = 1,
        
        /// <summary>
        /// Response to a G/M/T-code
        /// </summary>
        CodeReply = 2,
        
        /// <summary>
        /// Request execution of a macro file
        /// </summary>
        MacroRequest = 3,

        /// <summary>
        /// Request current file to be closed
        /// </summary>
        FileAbortRequest = 4,
        
        /// <summary>
        /// Stack has been pushed
        /// </summary>
        StackPushed = 5,
        
        /// <summary>
        /// Stack has been popped
        /// </summary>
        StackPopped = 6,
        
        /// <summary>
        /// Print has been paused
        /// </summary>
        PrintPaused = 7,
        
        /// <summary>
        /// Response to a heightmap request
        /// </summary>
        Heightmap = 8
    }
}