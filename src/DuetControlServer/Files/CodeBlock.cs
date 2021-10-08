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
        /// Constructor of this class
        /// </summary>
        /// <param name="startingCode">Code starting this block</param>
        /// <param name="processBlock">Whether instructions from this block may be processed</param>
        public CodeBlock(Code startingCode, bool processBlock)
        {
            StartingCode = startingCode;
            ProcessBlock = processBlock;
        }

        /// <summary>
        /// Code starting this block
        /// </summary>
        public Code StartingCode { get; }

        /// <summary>
        /// Check if the given indentation level finishes this block
        /// </summary>
        /// <param name="indent">Current indentation level</param>
        /// <returns>Whether the block is complete</returns>
        public bool IsFinished(int indent)
        {
            if (StartingCode.Keyword == DuetAPI.Commands.KeywordType.Var)
            {
                return indent < StartingCode.Indent;
            }
            return indent <= StartingCode.Indent;
        }

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
        /// Indicates if this block contains local variables
        /// </summary>
        public bool HasLocalVariables { get; set; }

        /// <summary>
        /// List of local variables
        /// </summary>
        public List<string> LocalVariables { get; } = new List<string>();
    }
}
