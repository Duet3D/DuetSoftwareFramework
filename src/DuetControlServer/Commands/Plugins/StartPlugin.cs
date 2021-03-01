using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
    /// </summary>
    public sealed class StartPlugin : DuetAPI.Commands.StartPlugin
    {
        /// <summary>
        /// Lock to be used when a plugin is started to avoid race conditions
        /// </summary>
        private static readonly AsyncLock _startLock = new AsyncLock();

        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            using (await _startLock.LockAsync(Program.CancellationToken))
            {
                await Start(Plugin);
            }
        }

        /// <summary>
        /// Start a plugin (as a dependency)
        /// </summary>
        /// <param name="name">Plugin name</param>
        /// <param name="requiredBy">Plugin that requires this plugin</param>
        /// <returns>Whether the plugin could be found</returns>
        private async Task Start(string name, string requiredBy = null)
        {
            bool pluginFound = false, rootPlugin = false;
            List<string> dependencies = new List<string>();
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        // Don't do anything if the plugin is already running or if it cannot be started on the SBC
                        if (item.Pid > 0 || string.IsNullOrEmpty(item.SbcExecutable))
                        {
                            return;
                        }

                        // Start plugin dependencies
                        foreach (string dependency in item.SbcPluginDependencies)
                        {
                            if (dependency != requiredBy)
                            {
                                dependencies.Add(dependency);
                            }
                        }

                        // Check the required DSF version
                        if (!PluginManifest.CheckVersion(Program.Version, item.SbcDsfVersion))
                        {
                            throw new ArgumentException($"Incompatible DSF version (requires {item.SbcDsfVersion}, got {Program.Version})");
                        }

                        // Check the required RRF version
                        if (!string.IsNullOrEmpty(item.RrfVersion))
                        {
                            if (Model.Provider.Get.Boards.Count > 0)
                            {
                                string rrfVersion = Model.Provider.Get.Boards[0].FirmwareVersion;
                                if (!PluginManifest.CheckVersion(rrfVersion, item.RrfVersion))
                                {
                                    throw new ArgumentException($"Incompatible RRF version (requires {item.RrfVersion}, got {rrfVersion})");
                                }
                            }
                            else
                            {
                                throw new ArgumentException("Failed to check RRF version");
                            }
                        }

                        // Got a plugin
                        pluginFound = true;
                        rootPlugin = item.SbcPermissions.HasFlag(SbcPermissions.SuperUser);
                        break;
                    }
                }
            }

            // Make sure the requested plugin exists
            if (!pluginFound)
            {
                if (requiredBy == null)
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
                throw new ArgumentException($"Dependency {name} of plugin {requiredBy} not found");
            }

            // Start all the dependencies first
            foreach (string dependency in dependencies)
            {
                await Start(dependency, name);
            }

            // Start the plugin via the plugin service. This will update the PID too
            StartPlugin startCommand = new StartPlugin() { Plugin = name };
            await IPC.Processors.PluginService.PerformCommand(startCommand, rootPlugin);
        }
    }
}

