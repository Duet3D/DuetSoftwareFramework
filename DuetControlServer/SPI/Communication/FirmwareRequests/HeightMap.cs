namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Header of heightmap reports
    /// </summary>
    public struct HeightMap
    {
        /// <summary>
        /// X start coordinate of the heightmap
        /// </summary>
        public float XMin;
        
        /// <summary>
        /// X end coordinate of the heightmap
        /// </summary>
        public float XMax;
        
        /// <summary>
        /// Spacing between the probe points in X direction
        /// </summary>
        public float XSpacing;
        
        /// <summary>
        /// Y start coordinate of the heightmap
        /// </summary>
        public float YMin;
        
        /// <summary>
        /// Y end coordinate of the heightmap
        /// </summary>
        public float YMax;
        
        /// <summary>
        /// Spacing between the probe points in Y direction
        /// </summary>
        public float YSpacing;

        /// <summary>
        /// Probing radius on delta geometries
        /// </summary>
        public float Radius;

        /// <summary>
        /// Number of probe points
        /// </summary>
        public uint NumPoints;
    }
}