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
        private NLog.Logger _logger;

        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            _logger = NLog.LogManager.GetLogger($"Plugin {Plugin}");

            // Get the plugin
            Plugin plugin = null;
            using (await Plugins.LockAsync())
            {
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Name == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }
            }
            if (plugin == null)
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

            string sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Name, "bin", architecture, plugin.SbcExecutable);
            if (!File.Exists(sbcExecutable))
            {
                sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Name, "bin", plugin.SbcExecutable);
                if (!File.Exists(sbcExecutable))
                {
                    throw new ArgumentException($"Cannot find executable {sbcExecutable}");
                }
            }

            using (await Plugins.LockAsync())
            {
                // Make sure the same process isn't started twice
                if (Plugins.Processes.ContainsKey(plugin.Name))
                {
                    return;
                }

                // Start the plugin process
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = sbcExecutable,
                    Arguments = plugin.SbcExecutableArguments,
                    WorkingDirectory = Path.GetDirectoryName(sbcExecutable),
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                Process process = Process.Start(startInfo);
                DataReceivedEventHandler outputHandler = MakeOutputHandler(Plugin, MessageType.Success, plugin.SbcOutputRedirected);
                DataReceivedEventHandler errorHandler = MakeOutputHandler(Plugin, MessageType.Error, plugin.SbcOutputRedirected);
                process.OutputDataReceived += outputHandler;
                process.ErrorDataReceived += errorHandler;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Update the PID
                plugin.Pid = process.Id;
                Plugins.Processes[plugin.Name] = process;
                _logger.Info("Process has been started (pid {0})", process.Id);
                using (CommandConnection connection = new CommandConnection())
                {
                    await connection.Connect(Settings.SocketPath, Program.CancellationToken);
                    await connection.SetPluginProcess(plugin.Name, process.Id, Program.CancellationToken);
                }

                // Wait for the plugin to terminate in the background
                _ = Task.Run(async delegate
                {
                    try
                    {
                        // Wait for it to be terminated
                        await process.WaitForExitAsync();
                        process.ErrorDataReceived -= errorHandler;
                        process.OutputDataReceived -= outputHandler;

                        // Update the PID again
                        using (await Plugins.LockAsync())
                        {
                            foreach (Plugin item in Plugins.List)
                            {
                                if (item.Name == Plugin)
                                {
                                    _logger.Info("Process has been stopped with exit code {0}", process.ExitCode);
                                    item.Pid = -1;

                                    if (!Program.CancellationToken.IsCancellationRequested)
                                    {
                                        using CommandConnection connection = new CommandConnection();
                                        await connection.Connect(Settings.SocketPath, Program.CancellationToken);
                                        await connection.SetPluginProcess(plugin.Name, -1, Program.CancellationToken);
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
                            Plugins.Processes.Remove(plugin.Name);
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
        /// <param name="outputMessages">Output messages through the object model</param>
        /// <returns>Event handler</returns>
        private DataReceivedEventHandler MakeOutputHandler(string pluginName, MessageType messageType, bool outputMessages)
        {
            return (object sender, DataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    // Send messages to DSF
                    if (outputMessages)
                    {
                        try
                        {
                            using CommandConnection connection = new CommandConnection();
                            connection.Connect(Settings.SocketPath, Program.CancellationToken).Wait();
                            connection.WriteMessage(messageType, $"[{pluginName}]: {e.Data}").Wait();
                            return;
                        }
                        catch
                        {
                            _logger.Warn("Failed to send console message to DCS");
                        }
                    }

                    // Fall back to normal logging output via this service
                    if (messageType == MessageType.Error)
                    {
                        _logger.Error(e.Data);
                    }
                    else
                    {
                        _logger.Info(e.Data);
                    }
                }
            };
        }
    }
}
