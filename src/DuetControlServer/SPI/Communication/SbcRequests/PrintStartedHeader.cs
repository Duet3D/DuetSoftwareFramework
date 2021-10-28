using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Set file print info. This is followed by NumFilament floats representing
    /// the filament consumption and the actual name of the file being printed.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 40)]
    public struct PrintStartedHeader
    {
        /// <summary>
        /// Length of the filename
        /// </summary>
        public ushort FilenameLength;

        /// <summary>
        /// Length of the slicer
        /// </summary>
        public ushort GeneratedByLength;

        /// <summary>
        /// Number of filaments used
        /// </summary>
        public uint NumFilaments;

        /// <summary>
        /// Time the file was last modified (as time_t / 64-bit unsigned int).
        /// This is represented as the seconds elapsed since Jan 1 1970
        /// </summary>
        public ulong LastModifiedTime;

        /// <summary>
        /// Size of the file in bytes
        /// </summary>
        public uint FileSize;

        /// <summary>
        /// Height of the first layer in mm
        /// </summary>
        public float FirstLayerHeight;

        /// <summary>
        /// Height of the layers in mm
        /// </summary>
        public float LayerHeight;

        /// <summary>
        /// Total object height in mm
        /// </summary>
        public float ObjectHeight;
        
        /// <summary>
        /// Total print time in seconds
        /// </summary>
        public uint PrintTime;

        /// <summary>
        /// Simulated print time in seconds
        /// </summary>
        public uint SimulatedTime;
    }
}