using DuetControlServer.SPI.Communication.Shared;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Set an arbitrary object model value that is accessible via a field path.
    /// This struct is followed by the UTF-8 path to the object model value
    /// and optionally the value as string / expression.
    /// </summary>
    /// <remarks>
    /// This is unused in protocol version 1
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct SetObjectModelHeader
    {
        /// <summary>
        /// Type of the value
        /// </summary>
        [FieldOffset(0)]
        public DataType Type;
        
        /// <summary>
        /// Length of the payload
        /// </summary>
        [FieldOffset(1)]
        public byte FieldLength;
        
        /// <summary>
        /// Value as integer
        /// </summary>
        [FieldOffset(4)]
        public int IntValue;
        
        /// <summary>
        /// Value as unsigned integer
        /// </summary>
        [FieldOffset(4)]
        public uint UIntValue;
        
        /// <summary>
        /// Value as float
        /// </summary>
        [FieldOffset(4)]
        public float FloatValue;
    }
}