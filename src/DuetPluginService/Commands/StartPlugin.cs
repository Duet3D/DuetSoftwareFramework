using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
    /// </summary>
    public sealed class StartPlugin : DuetAPI.Commands.StartPlugin
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private NLog.Logger? _logger;

        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            _logger = NLog.LogManager.GetLogger($"Plugin {Plugin}");

            // Get the plugin
            Plugin? plugin = null;
            using (await Plugins.LockAsync())
            {
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Id == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }
            }
            if (plugin is null)
            {
                throw new ArgumentException($"Plugin {Plugin} not found by {(Program.IsRoot ? "root service" : "service")}");
            }

            // Is this the right service to start the plugin?
            if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) != Program.IsRoot)
            {
                throw new InvalidOperationException("Wrong plugin service to start this plugin");
            }

            // Get the actual executable
            string architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.X64 => "x86_64",
                _ => "unknown"
            };

            string sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Id, "dsf", architecture, plugin.SbcExecutable!);
            if (!File.Exists(sbcExecutable))
            {
                sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Id, "dsf", plugin.SbcExecutable!);
                if (!File.Exists(sbcExecutable))
                {
                    throw new ArgumentException($"Cannot find executable {sbcExecutable}");
                }
            }

            using (await Plugins.LockAsync())
            {
                // Make sure the same process isn't started twice
                if (Plugins.Processes.ContainsKey(plugin.Id))
                {
                    return;
                }

                // Start the plugin process
                ProcessStartInfo startInfo = new()
                {
                    FileName = (plugin.SbcPythonDependencies.Count == 0) ? sbcExecutable : Settings.PythonLaunchCommand,
                    Arguments = (plugin.SbcPythonDependencies.Count == 0) ? plugin.SbcExecutableArguments : Settings.PythonLaunchArguments
                        .Replace("{pluginDir}", Path.Combine(Settings.PluginDirectory, plugin.Id))
                        .Replace("{command}", sbcExecutable)
                        .Replace("{args}", (plugin.SbcExecutableArguments ?? string.Empty).Replace("'", "\\'")),
                    WorkingDirectory = Path.GetDirectoryName(sbcExecutable),
                    RedirectStandardError = plugin.SbcOutputRedirected,
                    RedirectStandardOutput = plugin.SbcOutputRedirected
                };
                _logger.Info("Launching {0} {1}", startInfo.FileName, startInfo.Arguments);

                Process? process = Process.Start(startInfo) ?? throw new IOException($"Failed to create process {sbcExecutable}");
                DataReceivedEventHandler outputHandler = MakeOutputHandler(Plugin, MessageType.Success);
                DataReceivedEventHandler errorHandler = MakeOutputHandler(Plugin, MessageType.Error);
                if (plugin.SbcOutputRedirected)
                {
                    process.OutputDataReceived += outputHandler;
                    process.ErrorDataReceived += errorHandler;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }

                // Update the PID
                plugin.Pid = process.Id;
                Plugins.Processes[plugin.Id] = process;
                _logger.Info("Process has been started (pid {0})", process.Id);
                using (CommandConnection connection = new())
                {
                    await connection.Connect(Settings.SocketPath, Program.CancellationToken);
                    await connection.SetPluginProcess(plugin.Id, process.Id, Program.CancellationToken);
                }

                // Wait for the plugin to terminate in the background
                _ = Task.Run(async delegate
                {
                    try
                    {
                        // Wait for it to be terminated
                        await process.WaitForExitAsync(Program.CancellationToken);
                        if (plugin.SbcOutputRedirected)
                        {
                            process.ErrorDataReceived -= errorHandler;
                            process.OutputDataReceived -= outputHandler;
                        }

                        // Update the PID again
                        using (await Plugins.LockAsync())
                        {
                            foreach (Plugin item in Plugins.List)
                            {
                                if (item.Id == Plugin)
                                {
                                    _logger.Info("Process has been stopped with exit code {0}", process.ExitCode);
                                    item.Pid = -1;

                                    if (!Program.CancellationToken.IsCancellationRequested)
                                    {
                                        using CommandConnection connection = new();
                                        await connection.Connect(Settings.SocketPath, Program.CancellationToken);
                                        await connection.SetPluginProcess(plugin.Id, -1, Program.CancellationToken);
                                    }
                                    break;
                                }
                            }
                        }

                        // Kill any leftover child processes
                        process.Kill(true);
                    }
                    finally
                    {
                        using (await Plugins.LockAsync())
                        {
                            Plugins.Processes.Remove(plugin.Id);
                        }
                        process.Dispose();
                    }
                });
            }
        }

        /// <summary>
        /// Create a new handler to capture messages from stdin/stderr
        /// </summary>
        /// <param name="pluginName">Name of the plugin</param>
        /// <param name="messageType">Message type</param>
        /// <returns>Event handler</returns>
        private DataReceivedEventHandler MakeOutputHandler(string pluginName, MessageType messageType)
        {
            return (object sender, DataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    try
                    {
                        using CommandConnection connection = new();
                        connection.Connect(Settings.SocketPath, Program.CancellationToken).Wait();
                        connection.WriteMessage(messageType, $"[{pluginName}]: {e.Data}").Wait();
                        return;
                    }
                    catch
                    {
                        _logger!.Warn("Failed to send console message to DCS");
                    }
                }
            };
        }
    }
}
