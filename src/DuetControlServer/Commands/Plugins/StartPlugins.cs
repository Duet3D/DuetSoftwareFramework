using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StartPlugins"/> command
/// </summary>
/// <param name="codeFactory">Code factory</param>
/// <param name="commandFactory">Command factory</param>
/// <param name="dsfLogger">Internal logger</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
/// <param name="lifetime">Host application lifetime</param>
public sealed class StartPlugins(CodeFactory codeFactory,
    CommandFactory commandFactory,
    Utility.Logger dsfLogger,
    FilePathResolver filePathResolver,
    Model.ObjectModel model,
    IHostApplicationLifetime lifetime,
    IOptions<Settings> settings) : DuetAPI.Commands.StartPlugins
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Indicates if the plugins are being started
    /// </summary>
    private static readonly AsyncLock _startLock = new();

    /// <summary>
    /// Start all the plugins
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.PluginSupport)
        {
            return;
        }

        using (await _startLock.LockAsync(cancellationToken))
        {
            // Don't proceed if all the plugins have been started
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (model.State.PluginsStarted)
                {
                    return;
                }
            }

            // Start all plugins
            if (File.Exists(settings.Value.PluginsFilename))
            {
                await using FileStream fileStream = new(settings.Value.PluginsFilename, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
                using StreamReader reader = new(fileStream, Encoding.UTF8, false, settings.Value.FileBufferSize);
                while (!reader.EndOfStream)
                {
                    string? pluginName = await reader.ReadLineAsync(cancellationToken);
                    if (pluginName is null)
                    {
                        break;
                    }

                    try
                    {
                        StartPlugin startCommand = commandFactory.Create<StartPlugin>();
                        startCommand.Plugin = pluginName;
                        startCommand.SaveState = false;
                        await startCommand.ExecuteAsync(cancellationToken);
                    }
                    catch (Exception e)
                    {
                        _logger.Debug(e);
                        await dsfLogger.LogOutputAsync(MessageType.Error, $"Failed to start plugin {pluginName}: {e.Message}");
                    }
                }
            }

            // Wait for pending plugins to finish their start process
            bool waitingForStart;
            do
            {
                waitingForStart = true;
                using (await model.AccessReadOnlyAsync(cancellationToken))
                {
                    foreach (Plugin item in model.Plugins.Values)
                    {
                        if (item.Pid > 0 && item.SbcNotifyStarted && !item.Started)
                        {
                            waitingForStart = true;
                            break;
                        }
                    }
                }

                if (waitingForStart)
                {
                    // Wait for the next plugin to start or to stop, in case the plugin we're waiting for failed during start-up
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
                    await Task.WhenAny(
                        NotifyPluginStarted.PluginStartedEvent.WaitAsync(cts.Token),
                        SetPluginProcess.PluginStoppedEvent.WaitAsync(cts.Token)
                    );
                }
            }
            while (waitingForStart);

            // Plugins have been started...
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.State.PluginsStarted = true;
            }

            // Run dsf-config.g next. It must run asynchronously in case the SBC channel is busy at this point
            string dsfConfigFile = await filePathResolver.ToPhysicalAsync(FilePathResolver.DsfConfigFile, FileDirectory.System, cancellationToken);
            if (File.Exists(dsfConfigFile))
            {
                Code dsfConfigCode = codeFactory.Create();
                dsfConfigCode.Channel = DuetAPI.CodeChannel.SBC;
                dsfConfigCode.Flags = CodeFlags.Asynchronous;
                dsfConfigCode.Type = CodeType.MCode;
                dsfConfigCode.MajorNumber = 98;
                dsfConfigCode.Parameters = [
                    new('P', FilePathResolver.DsfConfigFile)
                ];
                await dsfConfigCode.ExecuteAsync(cancellationToken);
            }
        }
    }
}
