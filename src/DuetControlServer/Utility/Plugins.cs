using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Helper functions for plugin management
    /// </summary>
    public static class Plugins
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Check if the actual versino fufills a required version
        /// </summary>
        /// <param name="actual">Actual version</param>
        /// <param name="required">Required version</param>
        /// <returns>Whether the actual version fulfills teh requirement</returns>
        public static bool CheckVersion(string actual, string required)
        {
            if (!string.IsNullOrWhiteSpace(required))
            {
                string[] actualItems = actual.Split(new char[] { '.', '-', '+' });
                string[] requiredItems = required.Split(new char[] { '.', '-', '+' });
                for (int i = 0; i < Math.Min(actualItems.Length, requiredItems.Length); i++)
                {
                    if (actualItems[i] != requiredItems[i])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Start the plugins
        /// </summary>
        /// <param name="plugins">List of plugins to start</param>
        /// <returns></returns>
        public static async Task StartPlugins(IEnumerable<string> plugins)
        {
            foreach (string plugin in plugins)
            {
                _logger.Info("Starting plugin {0}", plugin);
                try
                {
                    StartPlugin startPlugin = new StartPlugin
                    {
                        Plugin = plugin
                    };
                    await startPlugin.Execute();
                }
                catch (Exception e)
                {
                    await Model.Provider.Output(MessageType.Error, $"Failed to start plugin {plugin}: {e.Message}");
                    _logger.Debug(e);
                }
            }
        }

        /// <summary>
        /// Stop the running plugins
        /// </summary>
        /// <returns>List of stopped plugins</returns>
        public static async Task<IEnumerable<string>> StopPlugins()
        {
            List<string> startedPlugins = new List<string>();
            foreach (Plugin plugin in Model.Provider.Get.Plugins)
            {
                if (plugin.Pid >= 0)
                {
                    startedPlugins.Add(plugin.Name);
                    if (plugin.Pid > 0)
                    {
                        _logger.Debug("Stopping plugin {0}", plugin.Name);
                        try
                        {
                            StopPlugin stopPlugin = new StopPlugin()
                            {
                                Plugin = plugin.Name
                            };
                            await stopPlugin.Execute();
                        }
                        catch (Exception e)
                        {
                            Model.Provider.Get.Messages.Add(new Message(MessageType.Error, $"Failed to stop plugin {plugin}: {e.Message}"));
                            _logger.Debug(e);
                        }
                    }
                }
            }
            return startedPlugins;
        }
    }
}
