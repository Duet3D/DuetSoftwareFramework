using DuetControlServer.SPI.Communication.Shared;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Binary representation of the result of an evaluated expression
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct EvaluationResultHeader
    {
        /// <summary>
        /// Type of the expression
        /// </summary>
        [FieldOffset(0)]
        public DataType Type;

        /// <summary>
        /// Length of the following expression
        /// </summary>
        [FieldOffset(2)]
        public ushort ExpressionLength;

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
