using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Open a file for reading and/or writing
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct OpenFileHeader
    {
        /// <summary>
        /// Open the given file for writing. May be set to false to open a file in read-only mode
        /// </summary>
        [FieldOffset(0)]
        public byte ForWriting;

        /// <summary>
        /// If the file is opened for writing, this may specify if content is supposed to be appended
        /// </summary>
        [FieldOffset(1)]
        public byte Append;

        /// <summary>
        /// Length of the following filename
        /// </summary>
        [FieldOffset(2)]
        public byte FilenameLength;

        /// <summary>
        /// If the file is opened for writing, this may specify how many bytes may be preallocated for the file
        /// </summary>
        [FieldOffset(4)]
        public uint PreAllocSize;
    }
}
