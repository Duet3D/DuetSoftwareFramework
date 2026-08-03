using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Codes;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files;

/// <summary>
/// Runs a macro file to completion on a code channel
/// </summary>
/// <remarks>
/// <para>
/// A macro executes on a stack level of its own: it is pushed onto the channel's pipeline before the
/// first of its codes is read, and popped when the last one has finished. That is what makes a flush
/// inside a macro wait for the macro's own codes rather than for whatever started it, and what lets a
/// macro call another one without the two interleaving.
/// </para>
/// <para>
/// Macros used to be opened because RepRapFirmware asked for one over SPI, and nothing has asked
/// since that link was removed. This is the replacement: the callers that need a macro run - M98, the
/// job lifecycle, the fallback for a code no handler recognises - ask for it here instead.
/// </para>
/// </remarks>
/// <param name="codeProcessor">Code processor owning the channel pipelines</param>
/// <param name="fileFactory">Creates the macro file</param>
/// <param name="filePathResolver">Resolves the macro's physical path</param>
/// <param name="logger">Logger</param>
public sealed class MacroRunner(
    CodeProcessor codeProcessor,
    FileFactory fileFactory,
    FilePathResolver filePathResolver,
    ILogger<MacroRunner> logger)
{
    /// <summary>
    /// How deeply macros may nest before a macro is refused
    /// </summary>
    /// <remarks>
    /// A macro that calls itself would otherwise push stack levels until the process runs out of
    /// memory. RepRapFirmware caps its own stack for the same reason
    /// </remarks>
    public const int MaxNesting = 10;

    /// <summary>
    /// Run a macro file and wait for it to finish
    /// </summary>
    /// <param name="channel">Channel to run the macro on</param>
    /// <param name="fileName">Virtual filename of the macro</param>
    /// <param name="startCode">Code that asked for the macro, if any</param>
    /// <param name="directory">Directory the macro is looked up in</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the macro was found and run, false if there is no such file</returns>
    /// <remarks>
    /// A missing macro is not an error here. Most of the macros the machine runs are optional - a
    /// machine with no <c>stop.g</c> simply has nothing to do when it stops - so whether that matters
    /// is the caller's decision, not this one's
    /// </remarks>
    public async ValueTask<bool> TryRunAsync(CodeChannel channel, string fileName, Code? startCode = null,
                                             FileDirectory directory = FileDirectory.System,
                                             CancellationToken cancellationToken = default)
    {
        if (codeProcessor.GetStackDepth(channel) >= MaxNesting)
        {
            logger.LogError("Refusing to start macro file {File} on channel {Channel}: macros are nested {Depth} deep",
                            fileName, channel, MaxNesting);
            return false;
        }

        string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, directory, cancellationToken);
        if (!File.Exists(physicalFile))
        {
            return false;
        }

        MacroFile? macro = fileFactory.CreateMacro(fileName, physicalFile, channel, startCode, startCode?.SourceConnection ?? 0);
        if (macro is null)
        {
            return false;
        }

        // The stack level has to exist before the macro starts reading, because its codes are routed
        // to the level whose file they belong to
        codeProcessor.Push(channel, macro);
        try
        {
            macro.Start(false);
            await macro.WaitForFinishAsync();
        }
        finally
        {
            // Popping completes the level's code queues, so it has to happen however the macro ended
            codeProcessor.Pop(channel);
        }
        return true;
    }
}
