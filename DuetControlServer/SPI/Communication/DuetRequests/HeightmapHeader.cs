namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Header for heightmap reports
    /// </summary>
    public struct HeightmapHeader
    {
        /// <summary>
        /// X start coordinate of the heightmap
        /// </summary>
        public float XStart;
        
        /// <summary>
        /// X end coordinate of the heightmap
        /// </summary>
        public float XEnd;
        
        /// <summary>
        /// Spacing between the probe points in X direction
        /// </summary>
        public float XSpacing;
        
        /// <summary>
        /// Y start coordinate of the heightmap
        /// </summary>
        public float YStart;
        
        /// <summary>
        /// Y end coordinate of the heightmap
        /// </summary>
        public float YEnd;
        
        /// <summary>
        /// Spacing between the probe points in Y direction
        /// </summary>
        public float YSpacing;
    }
}