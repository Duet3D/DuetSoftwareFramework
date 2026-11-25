using System.Collections.Immutable;
using DuetAPI.Commands;
using DuetControlServer.Codes.Meta;

namespace DuetControlServer.Files;

/// <summary>
/// What a <see cref="CodeFile"/> has to be put back to in order to be read again from one of its codes
/// </summary>
/// <param name="StartPosition">Where the code starts</param>
/// <param name="EndPosition">Where the code ends</param>
/// <param name="Start">State of the parser where the code starts</param>
/// <param name="End">State of the parser where the code ends</param>
/// <param name="Block">Innermost open code block</param>
/// <param name="LastBlock">Last code block that ended</param>
/// <param name="Variables">Local variables when the code ran, or null if it has not</param>
/// <remarks>
/// A job reads ahead of the machine, so by the time a pause rewinds the file the loop the rewind lands in may have
/// moved on to a later iteration, or ended. Every code holds one of these, so it only refers to what it describes
/// </remarks>
internal readonly record struct CodeFileState(long StartPosition, long EndPosition, CodeParserState Start, CodeParserState End,
                                              CodeBlock? Block, CodeBlock? LastBlock, ImmutableDictionary<string, LocalVariable>? Variables)
{
    /// <summary>
    /// State to start reading at a position that nothing is known about
    /// </summary>
    /// <param name="position">File position</param>
    /// <returns>The state</returns>
    public static CodeFileState At(long position)
    {
        CodeParserState parser = default(CodeParserState) with { LineNumber = (position == 0) ? 1 : null };
        return new CodeFileState(position, position, parser, parser, null, null, null);
    }
}
