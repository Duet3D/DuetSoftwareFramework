using DuetAPI.ObjectModel;
using DuetPluginService.Commands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService
{
    /// <summary>
    /// Main program class
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Version of this application
        /// </summary>
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Indicates if the application is running as root
        /// </summary>
        public static readonly bool IsRoot = (LinuxApi.Commands.GetEffectiveUserID() == 0) || (LinuxApi.Commands.GetEffectiveGroupID() == 0);

        /// <summary>
        /// Global cancellation source that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationTokenSource CancelSource = new CancellationTokenSource();

        /// <summary>
        /// Global cancellation token that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationToken CancellationToken = CancelSource.Token;

        /// <summary>
        /// Cancellation token to be called when the program has been terminated
        /// </summary>
        private static readonly ManualResetEvent _programTerminated = new ManualResetEvent(false);

        /// <summary>
        /// Entry point of the program
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        private static async Task Main(string[] args)
        {
            Console.WriteLine($"Duet Plugin Service v{Version}");
            Console.WriteLine("Written by Christian Hammacher for Duet3D");
            Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
            Console.WriteLine();

            // Initialize settings
            try
            {
                if (!Settings.Init(args))
                {
                    return;
                }
                _logger.Info("Settings loaded");
            }
            catch (Exception e)
            {
                _logger.Fatal(e, "Failed to load settings");
                return;
            }

            // Attempt to connect to the control server
            try
            {
                await IPC.Service.Connect();
                _logger.Info("Connection established");
            }
            catch (SocketException)
            {
                _logger.Fatal("Could not connect to DCS");
                return;
            }

            // Deal with program termination requests (SIGTERM and Ctrl+C)
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGTERM, shutting down...");
                    CancelSource.Cancel();
                    _programTerminated.WaitOne();
                }
            };
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGINT, shutting down...");
                    e.Cancel = true;
                    CancelSource.Cancel();
                }
            };

            // Notify the service manager that we're up and running.
            // It might take a while to process runonce.g so do it first
            string notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
            if (!string.IsNullOrEmpty(notifySocket))
            {
                try
                {
                    using Socket socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                    socket.Connect(new UnixDomainSocketEndPoint(notifySocket));
                    socket.Send(System.Text.Encoding.UTF8.GetBytes("READY=1"));
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to notify systemd about process start");
                }
            }

            // Register installed plugins
            foreach (string file in Directory.GetFiles(Settings.PluginDirectory))
            {
                if (file.EndsWith(".json"))
                {
                    try
                    {
                        using FileStream manifestStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                        Plugin plugin = new Plugin();
                        plugin.UpdateFromJson(manifestJson.RootElement);
                        plugin.Pid = -1;
                        using (await Plugins.LockAsync())
                        {
                            Plugins.List.Add(plugin);
                        }
                        _logger.Info("Plugin {0} loaded", plugin.Name);
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to load plugin manifest {0}", Path.GetFileName(file));
                    }
                }
            }

            // Wait for IPC task to terminate
            try
            {
                await IPC.Service.Run();
            }
            catch (Exception e)
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Fatal("Abnormal program termination");
                    _logger.Fatal(e);
                }
                CancelSource.Cancel();
            }

            // Stop the plugins again
            _logger.Info("Stopping plugins...");
            List<Task> stopTasks = new List<Task>();
            using (await Plugins.LockAsync())
            {
                foreach (Plugin plugin in Plugins.List)
                {
                    if (Plugins.Processes.ContainsKey(plugin.Name))
                    {
                        StopPlugin stopCommand = new StopPlugin() { Plugin = plugin.Name };
                        stopTasks.Add(Task.Run(stopCommand.Execute));
                    }
                }
            }
            await Task.WhenAll(stopTasks);
            _logger.Info("Plugins stopped");

            // End
            _logger.Info("Application has shut down");
            NLog.LogManager.Shutdown();
            _programTerminated.Set();
        }
    }
}
