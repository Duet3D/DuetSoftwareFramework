using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SystemTests.Host;

/// <summary>
/// One running bench: the scripted controller and the DuetControlServer started against it
/// </summary>
/// <remarks>
/// A bench boots with the CAN bus enabled and nothing else configured. A scenario adds only what it
/// needs, from the shared parts in <c>Host/Config</c> or as its own lines
/// </remarks>
internal sealed class Bench(ScriptedCanMaster canMaster, DcsTestHost host) : IAsyncDisposable
{
    /// <summary>The scripted controller</summary>
    public ScriptedCanMaster CanMaster { get; } = canMaster;

    /// <summary>The hosted DuetControlServer</summary>
    public DcsTestHost Host { get; } = host;

    /// <summary>Start a bench configured from the given parts, in order</summary>
    public static Task<Bench> StartAsync(params ConfigPart[] parts) => With(parts).StartAsync();

    /// <summary>Begin a bench configured from the given parts, to be scripted further before it starts</summary>
    public static BenchBuilder With(params ConfigPart[] parts) => new BenchBuilder().With(parts);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        CanMaster.Dispose();
    }
}

/// <summary>Everything a bench is set up with before it starts</summary>
internal sealed class BenchBuilder
{
    /// <summary>
    /// What every config.g starts with. With the bus disabled the configuration's own CAN messages
    /// would be answered with BusError, as the real controller answers them
    /// </summary>
    public const string EnableCan = "M953";

    private readonly List<ConfigPart> _parts = [];
    private readonly List<Action<VirtualSd>> _prepareSd = [];
    private readonly List<Action<ScriptedCanMaster>> _prepareController = [];
    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private SteppedTimeline? _timeline;
    private bool _keepConfigTraffic;

    /// <summary>Add parts to config.g</summary>
    public BenchBuilder With(params ConfigPart[] parts)
    {
        _parts.AddRange(parts);
        return this;
    }

    /// <summary>Populate the virtual SD card, after the parts have written their files</summary>
    public BenchBuilder WithSd(Action<VirtualSd>? prepare)
    {
        if (prepare is not null)
        {
            _prepareSd.Add(prepare);
        }
        return this;
    }

    /// <summary>Script the controller before the host connects to it</summary>
    public BenchBuilder WithController(Action<ScriptedCanMaster>? prepare)
    {
        if (prepare is not null)
        {
            _prepareController.Add(prepare);
        }
        return this;
    }

    /// <summary>Override <see cref="DuetControlServer.Settings"/> values, by property name</summary>
    public BenchBuilder WithSettings(IReadOnlyDictionary<string, string?> settings)
    {
        foreach ((string key, string? value) in settings)
        {
            _settings[key] = value;
        }
        return this;
    }

    /// <summary>
    /// Run the controller's clock from a timeline the test drives, rather than from the wall clock.
    /// The caller owns the timeline and must dispose it after the bench
    /// </summary>
    public BenchBuilder SteppedBy(SteppedTimeline timeline)
    {
        _timeline = timeline;
        return this;
    }

    /// <summary>
    /// Keep the link traffic config.g caused in the controller's capture, for a scenario about what
    /// starting up sends. Otherwise the capture starts empty once config.g has finished
    /// </summary>
    public BenchBuilder KeepingConfigTraffic()
    {
        _keepConfigTraffic = true;
        return this;
    }

    /// <summary>The config.g the bench boots from</summary>
    public string ConfigG
    {
        get
        {
            IEnumerable<string> lines = _parts.OrderBy(part => part.Order)          // stable, so in order within each
                                              .Select(part => part.Lines.Trim('\n'))
                                              .Where(text => text.Length > 0);
            return string.Join('\n', lines.Prepend(EnableCan)) + "\n" + DcsTestHost.ConfigDoneMarker;
        }
    }

    /// <summary>Start the bench in relative coordinates (G91), so that each move is the distance it commands</summary>
    public async Task<Bench> StartRelativeAsync()
    {
        Bench bench = await StartAsync();
        try
        {
            await bench.Host.ExecuteCodeAsync("G91");
        }
        catch
        {
            await bench.DisposeAsync();
            throw;
        }
        return bench;
    }

    /// <summary>
    /// Start the bench, with the controller answering every CAN request like a healthy board that took
    /// the setting unless the scenario scripted otherwise, wait for config.g to finish, and empty the
    /// capture of the traffic it caused unless told to keep it
    /// </summary>
    public async Task<Bench> StartAsync()
    {
        ScriptedCanMaster canMaster = new(ScriptedCanMaster.NewSocketPath(), _timeline?.Clock);
        DcsTestHost? host = null;
        try
        {
            canMaster.AckCanRequestsWithStandardReplies();
            foreach (Action<ScriptedCanMaster> prepare in _prepareController)
            {
                prepare(canMaster);
            }

            string configG = ConfigG;
            host = await DcsTestHost.StartAsync(canMaster, sd =>
            {
                sd.WriteSys("config.g", configG);
                foreach (ConfigPart part in _parts)
                {
                    part.Files?.Invoke(sd);
                }
                foreach (Action<VirtualSd> prepare in _prepareSd)
                {
                    prepare(sd);
                }
            }, _settings.Count > 0 ? new Dictionary<string, string?>(_settings) : null);

            // config.g may run macros and wait on the machine, which needs a stepped timeline moving under it
            if (_timeline is not null)
            {
                await _timeline.WhileRunningAsync(() => host.WaitForConfigDoneAsync());
            }
            else
            {
                await host.WaitForConfigDoneAsync();
            }
            if (!_keepConfigTraffic)
            {
                canMaster.ClearCapture();
            }
            return new Bench(canMaster, host);
        }
        catch
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            canMaster.Dispose();
            throw;
        }
    }
}
