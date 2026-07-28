using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetSharedLibrary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
/// </summary>
/// <param name="pluginStore">Plugin store</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Application settings</param>
public sealed class StartPlugin(PluginStore pluginStore, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.StartPlugin
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

        // Refuse to launch a non-SuperUser plugin if AppArmor is enabled but its profile is missing on disk - this
        // prevents a regen failure (or any other reason the profile is absent) from letting the plugin run unconfined
        if (!_settings.DisableAppArmor && !plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser))
        {
            string profilePath = Path.Combine(_settings.AppArmorProfileDirectory, $"dsf.{plugin.Id}");
            if (!File.Exists(profilePath))
            {
                throw new InvalidOperationException($"AppArmor profile for plugin {plugin.Id} is missing at {profilePath}; refusing to start");
            }
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

        string sbcExecutable = Path.Combine(settings.Value.PluginDirectory, plugin.Id, "dsf", architecture, plugin.SbcExecutable!);
        if (!File.Exists(sbcExecutable))
        {
            sbcExecutable = Path.Combine(settings.Value.PluginDirectory, plugin.Id, "dsf", plugin.SbcExecutable!);
            if (!File.Exists(sbcExecutable))
            {
                throw new ArgumentException($"Cannot find executable {sbcExecutable}");
            }
        }

        // Refuse to launch if the executable is a symlink. The plugin has write access to its own directory, so a
        // malicious plugin could swap its binary for a symlink to e.g. /bin/bash and escape its AppArmor profile on
        // next start (the kernel resolves symlinks before profile matching)
        if (new FileInfo(sbcExecutable).LinkTarget is not null)
        {
            throw new ArgumentException($"Refusing to launch plugin {plugin.Id}: executable {sbcExecutable} is a symlink");
        }

        // Python plugins are launched via a double-quoted bash -c string, so the executable path must not be able
        // to break out of the quoting. SbcExecutableArguments is restricted the same way at install time, but the
        // executable filename is plugin-controlled and only checked against ".." so far
        if (plugin.SbcPythonDependencies.Count > 0 && sbcExecutable.AsSpan().IndexOfAny(['"', '$', '`', '\\', '\n', '\r', ' ']) >= 0)
        {
            throw new ArgumentException($"Refusing to launch plugin {plugin.Id}: executable path contains shell metacharacters");
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
                    .Replace("{pluginDir}", Path.Combine(settings.Value.PluginDirectory, plugin.Id))
                    .Replace("{command}", sbcExecutable)
                    .Replace("{args}", plugin.SbcExecutableArguments ?? string.Empty),
                EnvironmentVariables =
                {
                    [Defaults.FullSocketPathEnvironmentVariable] = _settings.SocketPath
                },
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
            using (InternalCommandConnection connection = new())
            {
                await connection.ConnectAsync(_settings.SocketPath, cancellationToken);
                await connection.SetPluginProcessAsync(plugin.Id, process.Id, cancellationToken);
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
                    using (await pluginStore.LockAsync(lifetime.ApplicationStopped))
                    {
                        foreach (Plugin item in pluginStore.Plugins)
                        {
                            if (item.Id == Plugin)
                            {
                                logger.LogInformation("Process stopped with exit code {ExitCode}", process.ExitCode);
                                item.Pid = -1;

                                if (!cancellationToken.IsCancellationRequested)
                                {
                                    using InternalCommandConnection connection = new();
                                    await connection.ConnectAsync(_settings.SocketPath, cancellationToken);
                                    await connection.SetPluginProcessAsync(plugin.Id, -1, cancellationToken);
                                }
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    using (await pluginStore.LockAsync(lifetime.ApplicationStopped))
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
                await connection.ConnectAsync(_settings.SocketPath);
                await connection.WriteMessageAsync(messageType, $"[{pluginName}]: {e.Data}");
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
