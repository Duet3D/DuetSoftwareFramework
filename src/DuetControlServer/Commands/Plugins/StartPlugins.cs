﻿using DuetAPI.ObjectModel;
using System;
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
            if (File.Exists(Settings.PluginsFilename))
            {
                return;
            }

            using FileStream fileStream = new FileStream(Settings.PluginsFilename, FileMode.Open, FileAccess.Read);
            using StreamReader reader = new StreamReader(fileStream);
            while (!reader.EndOfStream)
            {
                string pluginName = await reader.ReadLineAsync();
                try
                {
                    StartPlugin startCommand = new StartPlugin() { Plugin = pluginName };
                    await startCommand.Execute();
                }
                catch (Exception e)
                {
                    _logger.Debug(e);
                    await Utility.Logger.LogOutput(MessageType.Error, $"Failed to start plugin {pluginName}: {e.Message}");
                }
            }
        }
    }
}