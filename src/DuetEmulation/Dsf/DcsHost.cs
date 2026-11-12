using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer;
using DuetControlServer.Commands;
using DuetRealtimeCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DuetEmulation.Dsf;

/// <summary>
/// DuetControlServer hosted in-process, with the real <c>libduet_realtime_core.so</c>, driving an
/// emulated controller over the emulator link: the SPI transport, with the bus and lines carried to the
/// controller's SPI model in Renode
/// </summary>
public sealed class DcsHost : IAsyncDisposable
{
    private static readonly Lazy<bool> NativeLibraryRegistered = new(() =>
    {
        HostLibraryLocator.Register(typeof(Settings).Assembly);
        return true;
    });

    private readonly IHost _host;
    private readonly FileLoggerProvider _log;

    /// <summary>The directory DuetControlServer treats as its SD card</summary>
    public string SdDirectory { get; }

    /// <summary>The host's services, for reaching any DuetControlServer component</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>The live object model</summary>
    public DuetControlServer.Model.ObjectModel Model => Services.GetRequiredService<DuetControlServer.Model.ObjectModel>();

    private DcsHost(IHost host, FileLoggerProvider log, string sdDirectory)
    {
        _host = host;
        _log = log;
        SdDirectory = sdDirectory;
    }

    /// <summary>
    /// Start DuetControlServer with <paramref name="files"/> on its SD card, connecting to the
    /// controller's SBC link at <paramref name="emulatorSocket"/>
    /// </summary>
    /// <param name="workDirectory">Where its SD card and log go</param>
    /// <param name="socketDirectory">Where its IPC socket goes, which must be a short path: a Unix socket's is limited to 107 bytes</param>
    /// <param name="emulatorSocket">The socket the controller's SPI model listens on</param>
    /// <param name="files">Files on the SD card, by path on the card ("sys/config.g")</param>
    /// <param name="cancellationToken">Cancels starting</param>
    public static async Task<DcsHost> StartAsync(string workDirectory, string socketDirectory, string emulatorSocket, IReadOnlyDictionary<string, string> files,
                                                 CancellationToken cancellationToken = default)
    {
        _ = NativeLibraryRegistered.Value;

        string root = Path.Combine(workDirectory, "dsf");
        string sd = Path.Combine(root, "sd");
        foreach (string directory in new[] { "sys", "gcodes", "macros", "firmware" })
        {
            Directory.CreateDirectory(Path.Combine(sd, directory));
        }
        foreach ((string path, string contents) in files)
        {
            string target = Path.Combine(sd, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, contents, cancellationToken);
        }
        Directory.CreateDirectory(Path.Combine(root, "plugins"));

        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Settings.SbcTransport)] = "Spi",
            [nameof(Settings.SpiHardware)] = "Emulator",
            [nameof(Settings.SpiEmulatorSocketPath)] = emulatorSocket,
            [nameof(Settings.BaseDirectory)] = sd,
            [nameof(Settings.SocketDirectory)] = socketDirectory,
            [nameof(Settings.StartErrorFile)] = Path.Combine(root, "dcs.err"),
            [nameof(Settings.PluginDirectory)] = Path.Combine(root, "plugins"),
            // TODO: these bound what the controller does in real time, and the emulator runs several
            // times slower than real time and not at a steady rate, so they are scaled up. Once
            // DuetControlServer takes its time from a TimeProvider they can be measured in emulated
            // time instead, which makes the DSF bench deterministic
            [nameof(Settings.SbcConnectTimeout)] = "30000",
            [nameof(Settings.SbcTransferTimeout)] = "5000",
            [nameof(Settings.SbcConnectionTimeout)] = "20000",
            [nameof(Settings.CanRequestTimeout)] = "15000",
            [nameof(Settings.ExpansionBoardTimeout)] = "50000",
        };

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        // The logging builder does not dispose a provider it is given, so the host does
        FileLoggerProvider log = new(Path.Combine(workDirectory, "dcs.log"));
        IHost host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders()
                .AddProvider(log)
                .SetMinimumLevel(LogLevel.Debug))
            .ConfigureServices(services => services
                .AddSettings(configuration, updateOnly: false, logLevel: LogLevel.Debug,
                             configFile: new FileInfo(Path.Combine(root, "config.json")),
                             socketDirectory: null, socketFile: null, baseDirectory: null,
                             startErrorFile: out _)
                .AddDuetControlServer())
            .Build();
        try
        {
            await host.StartAsync(cancellationToken);
        }
        catch
        {
            host.Dispose();
            log.Dispose();
            throw;
        }
        return new DcsHost(host, log, sd);
    }

    /// <summary>Run codes as if entered on the HTTP input, and return the reply once they have finished</summary>
    public Task<string> ExecuteCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        SimpleCode command = Services.GetRequiredService<CommandFactory>().Create<SimpleCode>();
        command.Code = code;
        command.Channel = DuetAPI.CodeChannel.HTTP;
        return command.ExecuteAsync(cancellationToken);
    }

    /// <summary>Read part of the object model under its lock</summary>
    public async Task<T> ReadModelAsync<T>(Func<DuetControlServer.Model.ObjectModel, T> read, CancellationToken cancellationToken = default)
    {
        using (await Model.AccessReadOnlyAsync(cancellationToken))
        {
            return read(Model);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(30));
            await _host.StopAsync(stopTimeout.Token);
        }
        finally
        {
            _host.Dispose();
            _log.Dispose();
        }
    }
}
