using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Request to evaluate an expression
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct EvaluateExpressionHeader
    {
        /// <summary>
        /// Message type describing the message
        /// </summary>
        /// <seealso cref="MessageType"/>
        [FieldOffset(0)]
        public CodeChannel Channel;

        /// <summary>
        /// Length of the expression to evaluate in bytes
        /// </summary>
        [FieldOffset(2)]
        public ushort ExpressionLength;
    }
}
