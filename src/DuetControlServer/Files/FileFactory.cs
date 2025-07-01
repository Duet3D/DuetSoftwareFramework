using System;
using System.IO;
using DuetAPI;
using DuetControlServer.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Files;

/// <summary>
/// Factory for creating code and macro files
/// </summary>
/// <param name="serviceProvider">Service provider</param>
public class FileFactory(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

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
                _logger.Info("Starting macro file {0} on channel {1}", virtualFile, channel);
            }
            else
            {
                _logger.Debug("Starting macro file {0} on channel {1}", virtualFile, channel);
            }
            return macro;
        }
        catch (FileNotFoundException)
        {
            if (channel != CodeChannel.Daemon)
            {
                _logger.Debug("Macro file {0} not found", virtualFile);
            }
            else
            {
                _logger.Trace("Macro file {0} not found", virtualFile);
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to start macro file {0}: {1}", virtualFile, e.Message);
        }
        return null;
    }

    /// <summary>
    /// Create a macro file for execution on the given channel from an existing macro file
    /// </summary>
    /// <param name="fileName">Virtual file path</param>
    /// <param name="physicalFile">Physical file path</param>
    /// <param name="channel">Code requesting the macro</param>
    /// <param name="startCode">Code starting the macro file</param>
    /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
    /// <returns>Macro file or null if it could not be opened</returns>

    public MacroFile CreateMacro(MacroFile copyFrom, CodeChannel channel) => ActivatorUtilities.CreateInstance<MacroFile>(serviceProvider, copyFrom, channel);
}
