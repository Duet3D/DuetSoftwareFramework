using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.Singletons;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
/// </summary>
/// <param name="pluginStore">Plugin store</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="hostEnvironment">Host environment</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Application settings</param>
public sealed class StartPlugin(PluginStore pluginStore, IHostApplicationLifetime lifetime, IHostEnvironment hostEnvironment, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.StartPlugin
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Start a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is invalid</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ILogger logger = loggerFactory.CreateLogger($"Plugin {Plugin}");

        // Get the plugin
        Plugin? plugin = null;
        using (await pluginStore.LockAsync(cancellationToken))
        {
            foreach (Plugin item in pluginStore.Plugins)
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
            throw new ArgumentException($"Plugin {Plugin} not found by {(Environment.IsPrivilegedProcess ? "root service" : "service")}");
        }

        // Is this the right service to start the plugin?
        if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) != Environment.IsPrivilegedProcess)
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

        string sbcExecutable = Path.Combine(hostEnvironment.ContentRootPath, plugin.Id, "dsf", architecture, plugin.SbcExecutable!);
        if (!File.Exists(sbcExecutable))
        {
            sbcExecutable = Path.Combine(hostEnvironment.ContentRootPath, plugin.Id, "dsf", plugin.SbcExecutable!);
            if (!File.Exists(sbcExecutable))
            {
                throw new ArgumentException($"Cannot find executable {sbcExecutable}");
            }
        }

        using (await pluginStore.LockAsync(cancellationToken))
        {
            // Make sure the same process isn't started twice
            if (pluginStore.Processes.ContainsKey(plugin.Id))
            {
                return;
            }

            // Start the plugin process
            ProcessStartInfo startInfo = new()
            {
                FileName = (plugin.SbcPythonDependencies.Count == 0) ? sbcExecutable : _settings.PythonLaunchCommand,
                Arguments = (plugin.SbcPythonDependencies.Count == 0) ? plugin.SbcExecutableArguments : _settings.PythonLaunchArguments
                    .Replace("{pluginDir}", Path.Combine(hostEnvironment.ContentRootPath, plugin.Id))
                    .Replace("{command}", sbcExecutable)
                    .Replace("{args}", (plugin.SbcExecutableArguments ?? string.Empty).Replace("'", "\\'")),
                WorkingDirectory = Path.GetDirectoryName(sbcExecutable),
                RedirectStandardError = plugin.SbcOutputRedirected,
                RedirectStandardOutput = plugin.SbcOutputRedirected
            };
            logger.LogInformation("Launching {File} {Arguments}", startInfo.FileName, startInfo.Arguments);

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
            pluginStore.Processes[plugin.Id] = process;
            logger.LogInformation("Process started (pid {Pid})", process.Id);
            using (CommandConnection connection = new())
            {
                await connection.Connect(_settings.SocketPath, cancellationToken);
                await connection.SetPluginProcess(plugin.Id, process.Id, cancellationToken);
            }

            // Wait for the plugin to terminate in the background
            _ = Task.Run(async delegate
            {
                try
                {
                    // Wait for it to be terminated
                    await process.WaitForExitAsync(lifetime.ApplicationStopped);
                    if (plugin.SbcOutputRedirected)
                    {
                        process.ErrorDataReceived -= errorHandler;
                        process.OutputDataReceived -= outputHandler;
                    }

                    // Update the PID again
                    using (await pluginStore.LockAsync(cancellationToken))
                    {
                        foreach (Plugin item in pluginStore.Plugins)
                        {
                            if (item.Id == Plugin)
                            {
                                logger.LogInformation("Process stopped with exit code {ExitCode}", process.ExitCode);
                                item.Pid = -1;

                                if (!lifetime.ApplicationStopping.IsCancellationRequested)
                                {
                                    using CommandConnection connection = new();
                                    await connection.Connect(_settings.SocketPath, cancellationToken);
                                    await connection.SetPluginProcess(plugin.Id, -1, cancellationToken);
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
                    using (await pluginStore.LockAsync(cancellationToken))
                    {
                        pluginStore.Processes.Remove(plugin.Id);
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
        return async void (object sender, DataReceivedEventArgs e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            try
            {
                using CommandConnection connection = new();
                await connection.Connect(_settings.SocketPath);
                await connection.WriteMessage(messageType, $"[{pluginName}]: {e.Data}");
            }
            catch
            {
                loggerFactory
                    .CreateLogger($"Plugin {pluginName}")
                    .LogWarning("Failed to send console message to DCS");
            }
        };
    }
}
