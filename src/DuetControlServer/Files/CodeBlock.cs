using DuetControlServer.Commands;
using System.Collections.Generic;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Class representing a code block in a conditional G-code file
    /// </summary>
    public sealed class CodeBlock
    {
        /// <summary>
        /// Code starting this block
        /// </summary>
        public Code StartingCode { get; set; }

        /// <summary>
        /// Last evaluation result of the conditional start code indicating if this block is supposed to be processed
        /// </summary>
        public bool ProcessBlock { get; set; }

        /// <summary>
        /// Whether any codes or echo instructions have been executed so far
        /// </summary>
        public bool SeenCodes { get; set; }

        /// <summary>
        /// Indicates if the corresponding condition may be followed by elif/else
        /// </summary>
        public bool ExpectingElse { get; set; }

        /// <summary>
        /// Indicates if continue was called in a while loop
        /// </summary>
        public bool ContinueLoop { get; set; }

        /// <summary>
        /// Number of times this code block has been run so far
        /// </summary>
        public int Iterations { get; set; }

        /// <summary>
        /// List of local variables
        /// </summary>
        public List<string> LocalVariables { get; } = new List<string>();
    }
}
