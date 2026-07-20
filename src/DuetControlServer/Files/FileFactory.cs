using System;
using System.IO;
using DuetAPI;
using DuetControlServer.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Files;

/// <summary>
/// Factory for creating code and macro files
/// </summary>
/// <param name="logger">Logger instance</param>
/// <param name="serviceProvider">Service provider</param>
public class FileFactory(ILogger<FileFactory> logger, IServiceProvider serviceProvider)
{
    /// <summary>
    /// Create a code file for execution on the given channel
    /// </summary>
    /// <param name="virtualFile">Virtual file path</param>
    /// <param name="physicalFile">Physical file path</param>
    /// <param name="channel">Code channel</param>
    /// <returns>Code file instance</returns>
    public CodeFile Create(string virtualFile, string physicalFile, CodeChannel channel)
    {
        return ActivatorUtilities.CreateInstance<CodeFile>(serviceProvider, new CodeFilePath(virtualFile, physicalFile), channel);
    }

    /// <summary>
    /// Create a code file copy for execution on the given channel
    /// </summary>
    /// <param name="copyFrom">Code file to copy</param>
    /// <param name="channel">Code channel</param>
    /// <returns>Code file instance</returns>
    public CodeFile Create(CodeFile copyFrom, CodeChannel channel)
    {
        return ActivatorUtilities.CreateInstance<CodeFile>(serviceProvider, copyFrom, channel);
    }

    /// <summary>
    /// Create a macro file for execution on the given channel
    /// </summary>
    /// <param name="virtualFile">Virtual file path</param>
    /// <param name="physicalFile">Physical file path</param>
    /// <param name="channel">Code requesting the macro</param>
    /// <param name="startCode">Code starting the macro file</param>
    /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
    /// <returns>Macro file or null if it could not be opened</returns>

    public MacroFile? CreateMacro(string virtualFile, string physicalFile, CodeChannel channel, Code? startCode = null, int sourceConnection = 0)
    {
        try
        {
            MacroFile macro = (startCode == null)
                ? ActivatorUtilities.CreateInstance<MacroFile>(serviceProvider, new CodeFilePath(virtualFile, physicalFile), channel, sourceConnection)
                : ActivatorUtilities.CreateInstance<MacroFile>(serviceProvider, new CodeFilePath(virtualFile, physicalFile), channel, startCode, sourceConnection);

            if (channel != CodeChannel.Daemon)
            {
                logger.LogInformation("Starting macro file {File} on channel {Channel}", virtualFile, channel);
            }
            else
            {
                logger.LogDebug("Starting macro file {File} on channel {Channel}", virtualFile, channel);
            }
            return macro;
        }
        catch (FileNotFoundException)
        {
            if (channel != CodeChannel.Daemon)
            {
                logger.LogDebug("Macro file {File} not found", virtualFile);
            }
            else
            {
                logger.LogTrace("Macro file {File} not found", virtualFile);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to start macro file {File}", virtualFile);
        }
        return null;
    }

    /// <summary>
    /// Create a macro file for execution on the given channel from an existing macro file
    /// </summary>
    /// <param name="copyFrom">Macro file to copy from</param>
    /// <param name="channel">Code channel requesting the macro</param>
    /// <returns>Macro file or null if it could not be opened</returns>

    public MacroFile CreateMacro(MacroFile copyFrom, CodeChannel channel) => ActivatorUtilities.CreateInstance<MacroFile>(serviceProvider, copyFrom, channel);
}
