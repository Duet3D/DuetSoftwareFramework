using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Header of a variable assignment. This is followed by the variable name and expression
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct SetVariableHeader
    {
        /// <summary>
        /// Source of this request
        /// </summary>
        public CodeChannel Channel;

        /// <summary>
        /// Create a new variable or update an existing one
        /// </summary>
        public byte CreateVariable;

        /// <summary>
        /// Indicates
        /// </summary>
        public byte VariableLength;

        /// <summary>
        /// Length of the variable content
        /// </summary>
        public byte ExpressionLength;
    }
}
