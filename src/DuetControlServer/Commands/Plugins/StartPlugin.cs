using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly AsyncLock _startLock = new();

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

            // Start the plugin and its dependencies
            using (await _startLock.LockAsync(Program.CancellationToken))
            {
                await Start(Plugin);
            }

            // Save the execution state if requested
            if (SaveState)
            {
                using FileStream fileStream = new(Settings.PluginsFilename, FileMode.Create, FileAccess.Write);
                using StreamWriter writer = new(fileStream);
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    foreach (Plugin item in Model.Provider.Get.Plugins.Values)
                    {
                        if (item.Pid >= 0)
                        {
                            await writer.WriteLineAsync(item.Id);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Start a plugin (as a dependency)
        /// </summary>
        /// <param name="id">Plugin identifier</param>
        /// <param name="requiredBy">Plugin that requires this plugin</param>
        /// <returns>Whether the plugin could be found</returns>
        private async Task Start(string id, string requiredBy = null)
        {
            bool rootPlugin = false;
            List<string> dependencies = new();
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Plugins.TryGetValue(Plugin, out Plugin plugin))
                {
                    // Don't do anything if the plugin is already running or if it cannot be started on the SBC
                    if (plugin.Pid > 0 || string.IsNullOrEmpty(plugin.SbcExecutable))
                    {
                        return;
                    }

                    // Start plugin dependencies
                    foreach (string dependency in plugin.SbcPluginDependencies)
                    {
                        if (dependency != requiredBy)
                        {
                            dependencies.Add(dependency);
                        }
                    }

                    // Check the required DSF version
                    if (!PluginManifest.CheckVersion(Program.Version, plugin.SbcDsfVersion))
                    {
                        throw new ArgumentException($"Incompatible DSF version (requires {plugin.SbcDsfVersion}, got {Program.Version})");
                    }

                    // Check the required RRF version
                    if (!string.IsNullOrEmpty(plugin.RrfVersion))
                    {
                        if (Model.Provider.Get.Boards.Count > 0)
                        {
                            string rrfVersion = Model.Provider.Get.Boards[0].FirmwareVersion;
                            if (!PluginManifest.CheckVersion(rrfVersion, plugin.RrfVersion))
                            {
                                throw new ArgumentException($"Incompatible RRF version (requires {plugin.RrfVersion}, got {rrfVersion})");
                            }
                        }
                        else
                        {
                            throw new ArgumentException("Failed to check RRF version");
                        }
                    }

                    // Got a plugin
                    rootPlugin = plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser);
                }
                else
                {
                    if (requiredBy == null)
                    {
                        throw new ArgumentException($"Plugin {Plugin} not found");
                    }
                    throw new ArgumentException($"Dependency {id} of plugin {requiredBy} not found");
                }
            }

            // Start all the dependencies first
            foreach (string dependency in dependencies)
            {
                await Start(dependency, id);
            }

            // Start the plugin via the plugin service. This will update the PID too
            StartPlugin startCommand = new() { Plugin = id };
            await IPC.Processors.PluginService.PerformCommand(startCommand, rootPlugin);
        }
    }
}

