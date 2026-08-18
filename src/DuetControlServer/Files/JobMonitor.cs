using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Files;

/// <summary>
/// Keeps the <c>job</c> fields that describe how a job is getting on
/// </summary>
/// <remarks>
/// <para>
/// The port of RepRapFirmware's <c>PrintMonitor</c>. It answers one question - how long has this job
/// been going and how much longer will it take - and the whole of it turns on separating the time the
/// machine spent <em>printing</em> from the time it spent waiting: a job paused for ten minutes has
/// not printed for ten minutes, and an estimate that counted them would say the job had slowed down.
/// So warm-up and pause time are accumulated separately and taken back off.
/// </para>
/// <para>
/// A single writer, like <see cref="Model.MachineStatusService"/> and for the same reason: these are
/// derived values, and deriving them in the several places that change a condition is what makes
/// writers race. <see cref="JobProcessor"/> holds the conditions; this is the projection
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="jobProcessor">What the job is doing</param>
/// <param name="heatManager">Whether the machine is waiting for a heater, which is warm-up time</param>
/// <param name="logger">Logger</param>
internal sealed class JobMonitor(
    Model.ObjectModel model,
    JobProcessor jobProcessor,
    HeatManager heatManager,
    ILogger<JobMonitor> logger) : BackgroundService
{
    /// <summary>
    /// How often the job fields are brought up to date
    /// </summary>
    /// <remarks>RepRapFirmware's <c>UpdateIntervalMillis</c></remarks>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How much printing has to have happened before the rates are measured again
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>SnapshotIntervalSecondsPrinting</c>. Long enough that the rate is measured
    /// over a representative stretch of the file rather than over whichever move happened to be
    /// running, which is what makes the estimate stop jumping about
    /// </remarks>
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The same, while simulating, where there is nothing to smooth out
    /// </summary>
    private static readonly TimeSpan SimulatingSnapshotInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How much of the filament a job needs has to have gone through before a filament estimate means
    /// anything
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MinFilamentUsageForEstimation</c></remarks>
    private const float MinFilamentUsageForEstimation = 0.01f;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // When the job started and what it has spent not printing since
    private TimeSpan _startedAt;
    private TimeSpan _pausedAt;
    private TimeSpan _heatingStartedAt;
    private TimeSpan _totalPauseTime;
    private TimeSpan _totalWarmUpTime;
    private bool _paused;
    private bool _heatingUp;
    private bool _running;

    // The last measurement of how fast the job is getting through the file and the filament
    private TimeSpan _lastSnapshotAt;
    private TimeSpan _lastSnapshotNonPrintingTime;
    private float _lastSnapshotFileFraction;
    private float _lastSnapshotFilamentUsed;
    private float _fileProgressRate;
    private float _filamentProgressRate;

    // What the slicer said, and when, so the answer can age
    private float _slicerTimeLeft;
    private TimeSpan _slicerTimeLeftSetAt;

    /// <summary>
    /// Record what a slicer said is left, from M73
    /// </summary>
    /// <param name="secondsLeft">Seconds the slicer expects the job to take, or 0 to forget it</param>
    public void SetSlicerTimeLeft(float secondsLeft)
    {
        _slicerTimeLeft = secondsLeft;
        _slicerTimeLeftSetAt = _clock.Elapsed;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAsync(stoppingToken);
                await Task.Delay(UpdateInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // A figure that cannot be worked out must not stop it being worked out again
                logger.LogError(e, "Failed to update the job progress");
                await Task.Delay(UpdateInterval, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Bring the job fields up to date
    /// </summary>
    private async ValueTask UpdateAsync(CancellationToken cancellationToken)
    {
        bool processing, paused, simulating;
        using (await jobProcessor.LockAsync(cancellationToken))
        {
            processing = jobProcessor.IsProcessing || jobProcessor.PauseState != PauseState.NotPaused;
            paused = jobProcessor.PauseState != PauseState.NotPaused;
            simulating = jobProcessor.IsSimulating;
        }

        TimeSpan now = _clock.Elapsed;

        if (!processing)
        {
            if (_running)
            {
                await FinishAsync(cancellationToken);
            }
            return;
        }

        if (!_running)
        {
            Start(now);
        }

        // Time spent paused is not time spent printing. Whatever the job has been waiting for, the
        // rate it was getting through the file before it stopped is still the rate to estimate from
        if (paused)
        {
            if (!_paused)
            {
                _pausedAt = now;
                _paused = true;
            }
        }
        else
        {
            if (_paused)
            {
                TimeSpan pauseTime = now - _pausedAt;
                _totalPauseTime += pauseTime;
                _slicerTimeLeftSetAt += pauseTime;
                _paused = false;
            }

            // Nor is time spent waiting for a heater, which is what makes the start of a job look
            // slow if it is counted
            if (heatManager.IsWaitingForTemperatures)
            {
                if (!_heatingUp)
                {
                    _heatingStartedAt = now;
                    _heatingUp = true;
                }
            }
            else
            {
                if (_heatingUp)
                {
                    TimeSpan heatingTime = now - _heatingStartedAt;
                    _totalWarmUpTime += heatingTime;
                    _slicerTimeLeftSetAt += heatingTime;
                    _heatingUp = false;
                }

                await TakeSnapshotIfDueAsync(now, simulating, cancellationToken);
            }
        }

        await PublishAsync(now, cancellationToken);
    }

    /// <summary>
    /// Measure how fast the job is getting through the file and the filament, if enough has happened
    /// </summary>
    private async ValueTask TakeSnapshotIfDueAsync(TimeSpan now, bool simulating, CancellationToken cancellationToken)
    {
        TimeSpan nonPrintingTime = _totalWarmUpTime + _totalPauseTime;
        TimeSpan printedSinceSnapshot = (now - _lastSnapshotAt) - (nonPrintingTime - _lastSnapshotNonPrintingTime);
        if (printedSinceSnapshot < (simulating ? SimulatingSnapshotInterval : SnapshotInterval))
        {
            return;
        }

        float fraction = await FractionOfFilePrintedAsync(cancellationToken);
        float filamentUsed = await RawExtrusionAsync(cancellationToken);
        float seconds = (float)printedSinceSnapshot.TotalSeconds;

        _fileProgressRate = (fraction - _lastSnapshotFileFraction) / seconds;
        _filamentProgressRate = (filamentUsed - _lastSnapshotFilamentUsed) / seconds;
        _lastSnapshotFileFraction = fraction;
        _lastSnapshotFilamentUsed = filamentUsed;
        _lastSnapshotNonPrintingTime = nonPrintingTime;
        _lastSnapshotAt = now;
    }

    /// <summary>
    /// Write what the job has done and how long is left
    /// </summary>
    private async ValueTask PublishAsync(TimeSpan now, CancellationToken cancellationToken)
    {
        float duration = (float)(now - _startedAt - PauseTimeAt(now)).TotalSeconds;
        float warmUp = (float)WarmUpTimeAt(now).TotalSeconds;
        float pauseDuration = (float)PauseTimeAt(now).TotalSeconds;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.Job.Duration = (int)MathF.Round(duration);
            model.Job.WarmUpDuration = (int)MathF.Round(warmUp);
            model.Job.PauseDuration = (int)MathF.Round(pauseDuration);
            model.Job.FilePosition = await jobProcessor.GetFilePositionAsync(0, cancellationToken);

            float fraction = FractionOfFilePrinted(model);
            model.Job.TimesLeft.File = AsReportedTime(FileBasedEstimate(fraction));
            model.Job.TimesLeft.Filament = AsReportedTime(FilamentBasedEstimate(model));
            model.Job.TimesLeft.Slicer = AsReportedTime(SlicerBasedEstimate(now));
        }
    }

    /// <summary>
    /// How much longer the job will take, from how fast it is getting through the file
    /// </summary>
    private float FileBasedEstimate(float fraction)
        => _lastSnapshotAt != _startedAt && _fileProgressRate > 0.0f ? (1.0f - fraction) / _fileProgressRate : 0.0f;

    /// <summary>
    /// How much longer the job will take, from how fast it is using filament
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    private float FilamentBasedEstimate(Model.ObjectModel model)
    {
        if (_lastSnapshotAt == _startedAt || _filamentProgressRate <= 0.0f)
        {
            return 0.0f;
        }

        float needed = 0.0f;
        foreach (float filament in model.Job.File.Filament)
        {
            needed += filament;
        }

        float used = model.Job.RawExtrusion ?? 0.0f;
        if (needed <= 0.0f || used <= needed * MinFilamentUsageForEstimation)
        {
            return 0.0f;
        }

        // More filament than the file said means the job is as good as done, which is what
        // RepRapFirmware reports rather than a negative time
        return used >= needed ? 1.0f : (needed - used) / _filamentProgressRate;
    }

    /// <summary>
    /// How much longer the slicer said the job would take, aged by how long ago it said it
    /// </summary>
    private float SlicerBasedEstimate(TimeSpan now)
    {
        if (_slicerTimeLeft <= 0.0f)
        {
            return 0.0f;
        }

        // The slicer's figure counts printing time, so the time since it was given has to have the
        // waiting taken back out of it
        TimeSpan elapsed = now - _slicerTimeLeftSetAt;
        if (_heatingUp)
        {
            elapsed -= now - _heatingStartedAt;
        }
        if (_paused)
        {
            elapsed -= now - _pausedAt;
        }
        return MathF.Max(1.0f, _slicerTimeLeft - (float)elapsed.TotalSeconds);
    }

    /// <summary>
    /// An estimate as the object model reports it, which is null rather than zero when unknown
    /// </summary>
    private static int? AsReportedTime(float seconds) => seconds > 0.0f ? (int)MathF.Round(seconds) : null;

    /// <summary>
    /// How much of the job file has been read
    /// </summary>
    private async ValueTask<float> FractionOfFilePrintedAsync(CancellationToken cancellationToken)
    {
        long position = await jobProcessor.GetFilePositionAsync(0, cancellationToken);
        long length = jobProcessor.FileLength;
        return length > 0 ? (float)position / length : 0.0f;
    }

    /// <summary>
    /// The same, from what the model already holds
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    private static float FractionOfFilePrinted(Model.ObjectModel model)
    {
        long size = model.Job.File.Size;
        return size > 0 ? (float)(model.Job.FilePosition ?? 0L) / size : 0.0f;
    }

    /// <summary>
    /// How much filament the job has used
    /// </summary>
    private async ValueTask<float> RawExtrusionAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return model.Job.RawExtrusion ?? 0.0f;
        }
    }

    /// <summary>
    /// Pause time as it stands, including a pause that has not finished
    /// </summary>
    private TimeSpan PauseTimeAt(TimeSpan now) => _paused ? _totalPauseTime + (now - _pausedAt) : _totalPauseTime;

    /// <summary>
    /// Warm-up time as it stands, including warming that has not finished
    /// </summary>
    private TimeSpan WarmUpTimeAt(TimeSpan now)
        => _heatingUp ? _totalWarmUpTime + (now - _heatingStartedAt) : _totalWarmUpTime;

    /// <summary>
    /// Start counting for a new job
    /// </summary>
    private void Start(TimeSpan now)
    {
        _running = true;
        _startedAt = _lastSnapshotAt = now;
        _totalPauseTime = _totalWarmUpTime = _lastSnapshotNonPrintingTime = TimeSpan.Zero;
        _paused = _heatingUp = false;
        _lastSnapshotFileFraction = _lastSnapshotFilamentUsed = 0.0f;
        _fileProgressRate = _filamentProgressRate = 0.0f;
        _slicerTimeLeft = 0.0f;
    }

    /// <summary>
    /// Record what the job that has just ended did, and stop counting
    /// </summary>
    private async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        TimeSpan now = _clock.Elapsed;
        int duration = (int)MathF.Round((float)(now - _startedAt - PauseTimeAt(now)).TotalSeconds);
        int warmUp = (int)MathF.Round((float)WarmUpTimeAt(now).TotalSeconds);
        _running = false;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // What the job did, kept after it has gone. The live fields go back to null, because a
            // machine with no job has no duration rather than the last one's
            model.Job.LastDuration = duration;
            model.Job.LastWarmUpDuration = warmUp;
            model.Job.LastFileName = model.Job.File.FileName;

            model.Job.Duration = null;
            model.Job.WarmUpDuration = null;
            model.Job.PauseDuration = null;
            model.Job.FilePosition = null;
            model.Job.TimesLeft.File = model.Job.TimesLeft.Filament = model.Job.TimesLeft.Slicer = null;
        }
    }
}
