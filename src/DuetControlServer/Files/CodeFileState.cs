using DuetControlServer.Commands;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Class representing a stack level in a conditional G-code file
    /// </summary>
    public sealed class CodeFileState
    {
        /// <summary>
        /// Code starting this block
        /// </summary>
        public Code StartingCode { get; set; }

        /// <summary>
        /// Last evaluation result of the start code
        /// </summary>
        public object LastResult { get; set; }

        /// <summary>
        /// Number of times this block has been run
        /// </summary>
        public int Iterations { get; set; }
    }
}
