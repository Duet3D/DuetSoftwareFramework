using DuetAPI.Commands;

namespace DuetControlServer.Files;

/// <summary>
/// A code block that is open in a G-code file, and the blocks around it
/// </summary>
/// <remarks>
/// Immutable, so that every code can refer to the blocks it was read in (<see cref="CodeFileState"/>). Reading on replaces
/// a block by a changed copy of it
/// </remarks>
public sealed record CodeBlock
{
    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="startingCode">Code starting this block</param>
    /// <param name="processBlock">Whether instructions from this block may be processed</param>
    /// <param name="parent">Block around this one</param>
    public CodeBlock(Code startingCode, bool processBlock, CodeBlock? parent)
    {
        Indent = startingCode.Indent;
        FilePosition = startingCode.FilePosition;
        LineNumber = startingCode.LineNumber;
        Keyword = startingCode.Keyword;
        ProcessBlock = processBlock;
        Parent = parent;
        Depth = (parent?.Depth ?? 0) + 1;
    }

    /// <summary>
    /// Block around this one, or null if there is none
    /// </summary>
    public CodeBlock? Parent { get; private init; }

    /// <summary>
    /// How many blocks deep this one is, counting itself
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Indentation of this block
    /// </summary>
    public int Indent { get; }

    /// <summary>
    /// File position where the block started
    /// </summary>
    public long? FilePosition { get; }

    /// <summary>
    /// Line number where the block started
    /// </summary>
    public long? LineNumber { get; }

    /// <summary>
    /// Keyword starting the block
    /// </summary>
    public KeywordType Keyword { get; }

    /// <summary>
    /// Check if the given indentation level finishes this block
    /// </summary>
    /// <param name="indent">Current indentation level</param>
    /// <returns>Whether the block is complete</returns>
    public bool IsFinished(int indent) => (Keyword == KeywordType.Var) ? indent < Indent : indent <= Indent;

    /// <summary>
    /// Last evaluation result of the conditional start code indicating if this block is supposed to be processed
    /// </summary>
    public bool ProcessBlock { get; init; }

    /// <summary>
    /// Whether any codes or echo instructions have been executed so far
    /// </summary>
    public bool SeenCodes { get; init; }

    /// <summary>
    /// Indicates if the corresponding condition may be followed by elif/else
    /// </summary>
    public bool ExpectingElse { get; init; }

    /// <summary>
    /// Indicates if continue was called in a while loop
    /// </summary>
    public bool ContinueLoop { get; init; }

    /// <summary>
    /// Number of times this code block has been run so far
    /// </summary>
    public int Iterations { get; init; }

    /// <summary>
    /// Indicates if this block contains local variables
    /// </summary>
    public bool HasLocalVariables { get; init; }

    /// <summary>
    /// The innermost while loop, this block included
    /// </summary>
    public CodeBlock? Loop => (Keyword == KeywordType.While) ? this : Parent?.Loop;

    /// <summary>
    /// Whether this block is in a while loop that is going to run again, or is one
    /// </summary>
    public bool IsInsideActiveLoop
        => (Keyword == KeywordType.While && (ProcessBlock || ContinueLoop)) || Parent?.IsInsideActiveLoop == true;

    /// <summary>
    /// Skip the rest of this block and of the blocks around it, up to the innermost while loop
    /// </summary>
    /// <param name="continueLoop">Whether that loop goes on to its next iteration</param>
    /// <returns>This block with that done</returns>
    public CodeBlock Leave(bool continueLoop)
        => (Keyword == KeywordType.While)
            ? this with { ProcessBlock = false, ContinueLoop = continueLoop }
            : this with { ProcessBlock = false, Parent = Parent?.Leave(continueLoop) };
}
