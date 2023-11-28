using DuetAPI.ObjectModel;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InstallPlugin"/> command
    /// </summary>
    public sealed class SetPluginProcess : DuetAPI.Commands.SetPluginProcess
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Update the pid of a given plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            using (await Model.Provider.AccessReadWriteAsync())
            {
                if (Model.Provider.Get.Plugins.TryGetValue(Plugin, out Plugin plugin))
                {
                    if (plugin.Pid > 0 && Pid < 0 && plugin.SbcAutoRestart)
                    {
                        _ = Task.Run(async delegate
                        {
                            try
                            {
                                // Wait a moment to avoid excessive system load in case the plugin is broken
                                await Task.Delay(Settings.PluginAutoRestartInterval, Program.CancellationToken);

                                // Restart it
                                _logger.Info("Auto-restarting plugin {0}", Plugin);
                                await new StartPlugin() { Plugin = Plugin }.Execute();
                            }
                            catch (Exception e)
                            {
                                if (e is not OperationCanceledException)
                                {
                                    _logger.Error(e, "Failed to auto-restart plugin {0}", Plugin);
                                }
                            }
                        });
                    }
                    plugin.Pid = Pid;
                }
                else
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
            }
        }
    }
}
