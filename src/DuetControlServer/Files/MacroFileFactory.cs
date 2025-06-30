using System;
using System.IO;
using DuetAPI;
using DuetControlServer.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Files;

public class MacroFileFactory(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Create a macro file for execution on the given channel
    /// </summary>
    /// <param name="fileName">Filename of the macro</param>
    /// <param name="physicalFile">Physical path of the macro</param>
    /// <param name="channel">Code requesting the macro</param>
    /// <param name="startCode">Code starting the macro file</param>
    /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
    /// <returns>Macro file or null if it could not be opened</returns>

    public MacroFile? Create(string fileName, string physicalFile, CodeChannel channel, Code? startCode = null, int sourceConnection = 0)
    {
        try
        {
            MacroFile macro = ActivatorUtilities.CreateInstance<MacroFile>(serviceProvider, fileName, physicalFile, channel, startCode!, sourceConnection);
            if (channel != CodeChannel.Daemon)
            {
                _logger.Info("Starting macro file {0} on channel {1}", fileName, channel);
            }
            else
            {
                _logger.Debug("Starting macro file {0} on channel {1}", fileName, channel);
            }
            return macro;
        }
        catch (FileNotFoundException)
        {
            if (channel != CodeChannel.Daemon)
            {
                _logger.Debug("Macro file {0} not found", fileName);
            }
            else
            {
                _logger.Trace("Macro file {0} not found", fileName);
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to start macro file {0}: {1}", fileName, e.Message);
        }
        return null;
    }

    /// <summary>
    /// Create a macro file for execution on the given channel from an existing macro file
    /// </summary>
    /// <param name="fileName">Filename of the macro</param>
    /// <param name="physicalFile">Physical path of the macro</param>
    /// <param name="channel">Code requesting the macro</param>
    /// <param name="startCode">Code starting the macro file</param>
    /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
    /// <returns>Macro file or null if it could not be opened</returns>

    public MacroFile Create(MacroFile copyFrom, CodeChannel channel) => ActivatorUtilities.CreateInstance<MacroFile>(serviceProvider, copyFrom, channel);
}
