using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StartPlugins"/> command
    /// </summary>
    public sealed class StartPlugins : DuetAPI.Commands.StartPlugins
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Start all the plugins
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                return;
            }

            // Start all plugins
            if (File.Exists(Settings.PluginsFilename))
            {
                using FileStream fileStream = new(Settings.PluginsFilename, FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(fileStream);
                while (!reader.EndOfStream)
                {
                    string pluginName = await reader.ReadLineAsync();
                    try
                    {
                        StartPlugin startCommand = new() { Plugin = pluginName };
                        await startCommand.Execute();
                    }
                    catch (Exception e)
                    {
                        _logger.Debug(e);
                        await Utility.Logger.LogOutput(MessageType.Error, $"Failed to start plugin {pluginName}: {e.Message}");
                    }
                }
            }

            // Plugins have been started...
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.State.PluginsStarted = true;
            }

            // Run dsf-config.g next
            string dsfConfigFile = await FilePath.ToPhysicalAsync(FilePath.DsfConfigFile, FileDirectory.System);
            if (File.Exists(dsfConfigFile))
            {
                Code dsfConfigCode = new()
                {
                    Channel = DuetAPI.CodeChannel.SBC,
                    Type = DuetAPI.Commands.CodeType.MCode,
                    MajorNumber = 98,
                    Parameters = new List<DuetAPI.Commands.CodeParameter>
                    {
                        new DuetAPI.Commands.CodeParameter('P', FilePath.DsfConfigFile)
                    }
                };
                await dsfConfigCode.Execute();
            }
        }
    }
}
