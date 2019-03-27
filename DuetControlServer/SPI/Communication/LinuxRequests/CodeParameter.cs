using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct CodeParameter
    {
        /// <summary>
        /// Letter prefix of this parameter
        /// </summary>
        [FieldOffset(0)] public byte Letter;

        /// <summary>
        /// Type of the parameter
        /// </summary>
        [FieldOffset(1)] public DataType Type;
            
        /// <summary>
        /// Value as integer
        /// </summary>
        [FieldOffset(4)] public int IntValue;
        
        /// <summary>
        /// Value as unsigned integer
        /// </summary>
        [FieldOffset(4)] public uint UIntValue;
        
        /// <summary>
        /// Value as float
        /// </summary>
        [FieldOffset(4)] public float FloatValue;
    }
}