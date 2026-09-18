using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Events;
using DuetControlServer.Fans;
using DuetControlServer.Files;
using DuetControlServer.Heat;
using DuetControlServer.IPC;
using DuetControlServer.Link;
using DuetControlServer.Motion;
using DuetControlServer.Ports;
using DuetControlServer.Spindles;
using DuetControlServer.Model;
using DuetControlServer.Tools;
using DuetControlServer.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SystemTests.Host;

/// <summary>
/// DuetControlServer hosted in-process for a system test: the same service registrations as
/// Program.cs, the real <c>NativeLink</c> and <c>libduet_sbc.so</c>, pointed over the socket
/// transport at a <see cref="ScriptedCanMaster"/>, with a per-test virtual SD tree
/// </summary>
internal sealed class DcsTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _tempRoot;

    /// <summary>The virtual SD card the host runs against</summary>
    public VirtualSd Sd { get; }

    /// <summary>The host's service provider, for reaching any DCS service in assertions</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>The live object model</summary>
    public DuetControlServer.Model.ObjectModel Model => Services.GetRequiredService<DuetControlServer.Model.ObjectModel>();

    private DcsTestHost(IHost host, string tempRoot, VirtualSd sd)
    {
        _host = host;
        _tempRoot = tempRoot;
        Sd = sd;
    }

    /// <summary>
    /// Build and start a DuetControlServer against the given fake controller. The SD tree is
    /// populated by <paramref name="prepareSd"/> before anything starts, so config.g is in place
    /// when the startup files run
    /// </summary>
    /// <param name="controller">The fake controller to connect to</param>
    /// <param name="prepareSd">Populates the virtual SD card, typically with a config.g</param>
    /// <param name="settingsOverrides">Extra <see cref="Settings"/> values, by property name</param>
    public static async Task<DcsTestHost> StartAsync(ScriptedCanMaster controller,
                                                     Action<VirtualSd>? prepareSd = null,
                                                     Dictionary<string, string?>? settingsOverrides = null)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"dsf-systemtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(Path.Combine(tempRoot, "run"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "plugins"));
        VirtualSd sd = new(Path.Combine(tempRoot, "sd"));
        prepareSd?.Invoke(sd);

        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Settings.SbcTransport)] = "Socket",
            [nameof(Settings.SbcSocketPath)] = controller.SocketPath,
            [nameof(Settings.BaseDirectory)] = sd.Root,
            [nameof(Settings.SocketDirectory)] = Path.Combine(tempRoot, "run"),
            [nameof(Settings.StartErrorFile)] = Path.Combine(tempRoot, "dcs.err"),
            [nameof(Settings.PluginDirectory)] = Path.Combine(tempRoot, "plugins"),
            // Generous enough that a breakpoint on the managed side does not trip the reconnect
            // path; the recovery scenarios script their failures instead of relying on these
            [nameof(Settings.SbcConnectTimeout)] = "5000",
            [nameof(Settings.SbcTransferTimeout)] = "5000",
            [nameof(Settings.SbcConnectionTimeout)] = "10000",
        };
        if (settingsOverrides != null)
        {
            foreach ((string key, string? value) in settingsOverrides)
            {
                settings[key] = value;
            }
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        IHost host = new HostBuilder()
            // Into the captured log rather than the console: a passing test stays silent whatever
            // the runner streams, and a failing one prints the whole debug-level log (CapturedLog)
            .ConfigureLogging(logging => logging.AddProvider(new CapturedLog()).SetMinimumLevel(LogLevel.Debug).AddTracyIfProfiling())
            .ConfigureServices(services => services
                .AddSettings(configuration, updateOnly: false, logLevel: LogLevel.Debug,
                             configFile: new FileInfo(Path.Combine(tempRoot, "config.json")),
                             socketDirectory: null, socketFile: null, baseDirectory: null,
                             startErrorFile: out _)
                .AddCodes()
                .AddCommands()
                .AddEvents()
                .AddFiles()
                .AddIPC()
                .AddLink()
                .AddModel()
                .AddLinkAdapter()
                .AddMotion()
                .AddPorts()
                .AddSpindles()
                .AddFans()
                .AddHeat()
                .AddTools()
                .AddUtility())
            .Build();

        try
        {
            using CancellationTokenSource startTimeout = new(TimeSpan.FromSeconds(60));
            await host.StartAsync(startTimeout.Token);
        }
        catch
        {
            host.Dispose();
            TryDeleteTree(tempRoot);
            throw;
        }
        return new DcsTestHost(host, tempRoot, sd);
    }

    /// <summary>
    /// The line a test config.g ends with so <see cref="WaitForConfigDoneAsync"/> can see it finish
    /// </summary>
    public const string ConfigDoneMarker = "\nglobal configDone = 1\n";

    /// <summary>Wait until config.g has run to its <see cref="ConfigDoneMarker"/> last line</summary>
    public async Task WaitForConfigDoneAsync(int timeoutMs = 30_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            // Whether the marker is there, rather than what it says: it has not run yet for most of
            // this poll, and reading it through the interpreter would log a channel error per ask
            if (await ReadModelAsync(model => model.Global.ContainsKey("configDone")))
            {
                return;
            }
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException("config.g did not finish; its done marker was never set");
    }

    /// <summary>Wait until the machine reports the given status</summary>
    public async Task WaitForStatusAsync(DuetAPI.ObjectModel.MachineStatus status, int timeoutMs = 20_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        DuetAPI.ObjectModel.MachineStatus current;
        do
        {
            using (await Model.AccessReadOnlyAsync(CancellationToken.None))
            {
                current = Model.State.Status;
            }
            if (current == status)
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Machine status stayed {current}, expected {status}");
    }

    /// <summary>Read part of the object model under its lock</summary>
    public async Task<T> ReadModelAsync<T>(Func<DuetControlServer.Model.ObjectModel, T> read)
    {
        using (await Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            return read(Model);
        }
    }

    /// <summary>Run G-code as if entered on the given input, returning the reply text</summary>
    /// <remarks>
    /// Bounded so that a code the machine never finishes fails the test naming itself, rather than
    /// hanging the run
    /// </remarks>
    public async Task<string> ExecuteCodeAsync(string code, DuetAPI.CodeChannel channel = DuetAPI.CodeChannel.HTTP,
                                               int timeoutMs = 60_000)
    {
        SimpleCode command = Services.GetRequiredService<CommandFactory>().Create<SimpleCode>();
        command.Code = code;
        command.Channel = channel;
        try
        {
            return await command.ExecuteAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Code \"{code}\" did not finish within {timeoutMs} ms");
        }
    }

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
            TryDeleteTree(_tempRoot);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A straggling socket or log file is not worth failing a test over
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
