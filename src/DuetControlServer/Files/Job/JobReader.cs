using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Motion;
using DuetControlServer.Utility;
using Microsoft.Extensions.Logging;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files.Job;

/// <summary>
/// Where a reader has got to in its file
/// </summary>
/// <param name="Value">Byte offset of the end of the last code that completed</param>
/// <remarks>
/// A record rather than a field because a <c>volatile long</c> does not compile and a plain one
/// tears on the 32-bit ARM target: the reference is what is published, and the value it points at
/// never changes
/// </remarks>
internal sealed record ReaderPosition(long Value);

/// <summary>
/// Reads one stream of a job and executes what it reads
/// </summary>
/// <remarks>
/// <para>
/// Driven by commands and reporting events, and holding no job state of its own: it does not know
/// whether the job is paused, does not choose where to rewind to and does not decide when a job is
/// over. <see cref="JobController"/> decides all three and tells it.
/// </para>
/// <para>
/// It owns its file, its code pool, its read-ahead window and the generation token that stops the
/// read-ahead. Nothing outside holds that token, so no caller has to re-read it and no sequence ever
/// runs under it
/// </para>
/// </remarks>
internal sealed class JobReader
{
    private readonly ChannelWriter<JobCommand> _events;
    private readonly CodeFactory _codeFactory;
    private readonly CodeProcessor _codeProcessor;
    private readonly EventLogger _eventLogger;
    private readonly ILogger _logger;
    private readonly Queue<Code> _codePool = new();

    /// <summary>
    /// Constructor of a reader
    /// </summary>
    /// <param name="index">Motion system this stream belongs to</param>
    /// <param name="file">File to read from, which this reader owns</param>
    /// <param name="events">Where to report what the stream did</param>
    /// <param name="codeFactory">Code factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="eventLogger">Event logger, for errors in the file</param>
    /// <param name="logger">Logger</param>
    /// <param name="bufferedCodes">How many codes to read ahead</param>
    public JobReader(int index, CodeFile file, ChannelWriter<JobCommand> events, CodeFactory codeFactory,
                     CodeProcessor codeProcessor, EventLogger eventLogger, ILogger logger, int bufferedCodes)
    {
        Index = index;
        File = file;
        _events = events;
        _codeFactory = codeFactory;
        _codeProcessor = codeProcessor;
        _eventLogger = eventLogger;
        _logger = logger;

        for (int i = 0; i < Math.Max(bufferedCodes, 1); i++)
        {
            _codePool.Enqueue(_codeFactory.Create());
        }
    }

    /// <summary>
    /// Motion system this stream belongs to
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// The file being read from
    /// </summary>
    public CodeFile File { get; }

    /// <summary>
    /// Channel the codes go to
    /// </summary>
    public CodeChannel Channel => File.Channel;

    /// <summary>
    /// Byte offset of the end of the last code that completed
    /// </summary>
    /// <remarks>
    /// Published by the reader after every completed code and read directly by M27, M36 and
    /// <see cref="JobMonitor"/>: it is a fact about the file rather than a question for the
    /// controller, so asking for it costs no command and no lock
    /// </remarks>
    public long Position => _position.Value;
    private volatile ReaderPosition _position = new(0);

    /// <summary>
    /// Cancels the codes of the stretch of reading between a <c>Run</c> and the next freeze
    /// </summary>
    private CancellationTokenSource? _generation;

    /// <summary>
    /// The read-ahead loop, alive between a <c>Run</c> and the freeze or end of file that stops it
    /// </summary>
    private Task? _readTask;

    /// <summary>
    /// Whether the stretch of reading has been told to stop
    /// </summary>
    /// <remarks>
    /// Written by the controller and its sequences, read by the read-ahead loop, so both of these
    /// are volatile: they are what one task tells another about a stretch of work in flight
    /// </remarks>
    private volatile bool _frozen;

    /// <summary>
    /// Whether the freeze is the boundary one, which the reader reports having reached
    /// </summary>
    private volatile bool _frozenAtBoundary;

    /// <summary>
    /// Start reading, from the point the controller says the job carries on from
    /// </summary>
    /// <param name="from">Where to carry on from, or null to read on from where the file stands</param>
    /// <param name="restartMacro">
    /// Whether the first command is one the job is running again, which is RepRapFirmware's
    /// <c>firstCommandAfterRestart</c>
    /// </param>
    /// <param name="runToken">Token cancelled when the run this reading belongs to is torn down</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask RunAsync(JobResumePoint? from, bool restartMacro, CancellationToken runToken,
                                    CancellationToken cancellationToken)
    {
        using (await File.LockAsync(cancellationToken))
        {
            if (from is JobResumePoint point && point.GCommandNumber >= 0)
            {
                // A resumed line may be a bare "X100 Y100 E5", and what makes that a move is a G1
                // several lines above it. RepRapFirmware's SetModalGCommand
                File.ModalGCommand = point.GCommandNumber;
            }
            File.FirstCommandAfterRestart = restartMacro || File.Position > 0;
            File.HoldAtNextCode = false;
        }

        // The stream may have been read to its end before the pause, which cleared the channel's job
        // file as the reader drained. It reads from the same file again
        _codeProcessor.SetJobFile(Channel, File);

        _frozen = _frozenAtBoundary = false;
        _generation = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        _readTask = ReadAheadAsync(_generation.Token);
    }

    /// <summary>
    /// Whether the stream is reading and executing codes
    /// </summary>
    public bool IsReading => _readTask is not null && !_readTask.IsCompleted;

    /// <summary>
    /// Stop reading now, without waiting for what is in flight
    /// </summary>
    /// <remarks>
    /// The transition half of <see cref="CloseAsync"/>: the file is closed and the generation
    /// cancelled inside the controller loop, so nothing more is read the moment the phase changes,
    /// while the waiting is left to the sequence that follows. A loop that waited here could be held
    /// by the very code that asked for the transition
    /// </remarks>
    public void StopReading()
    {
        File.Close();
        Freeze();
    }

    /// <summary>
    /// Stop reading now
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancelling the generation is what lets a pause land during an <c>M109</c>: the codes not yet
    /// dispatched are dropped before dispatch by the check the pipeline already makes, and the ones
    /// inside a handler abort. The rewind puts them back.
    /// </para>
    /// <para>
    /// Returns at once, without waiting for the codes in flight: <see cref="DrainAsync"/> is that
    /// wait. They are two steps because a code the stream started may be waiting for a move the
    /// stop that follows this is about to drop, so a freeze that waited here would be waiting for
    /// the very thing it comes before
    /// </para>
    /// </remarks>
    public void Freeze()
    {
        _frozenAtBoundary = false;
        _frozen = true;
        _generation?.Cancel();
    }

    /// <summary>
    /// Wait for every code the stream started to finish, cancelled or not
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async ValueTask DrainAsync()
    {
        if (_readTask is not null)
        {
            await _readTask;
        }
    }

    /// <summary>
    /// Stop reading at the end of the code the stream is on
    /// </summary>
    /// <remarks>
    /// Returns at once: the code already inside a handler, and the macro it is running, are left to
    /// finish, and the reader reports <c>Stopped</c> when they have. The barrier is armed in the
    /// dispatch path rather than polled for, so the code that follows the macro is cancelled where
    /// it would have been started. RepRapFirmware makes the same check in <c>StartNextGCode</c>
    /// </remarks>
    public void FreezeAtBoundary()
    {
        if (_readTask is null || _frozen)
        {
            return;
        }
        _frozenAtBoundary = true;
        _frozen = true;
        File.HoldAtNextCode = true;
    }

    /// <summary>
    /// Set where the stream carries on from and report that it has stopped there
    /// </summary>
    /// <param name="position">Byte offset to read from next</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// Separate from the freeze because the rewind point is not known until the machine has stopped.
    /// A frozen reader answers this whether or not it had reached the end of the file, and never
    /// reports <c>Finished</c> after a freeze
    /// </remarks>
    public async ValueTask RewindAsync(long position, CancellationToken cancellationToken)
    {
        using (await File.LockAsync(cancellationToken))
        {
            File.Position = position;
        }
        Publish(position);
        _codeProcessor.ResolveSyncRequestsAfter(position);
    }

    /// <summary>
    /// Set where the stream starts from, without it having stopped
    /// </summary>
    /// <param name="position">Byte offset to read from next</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>M26, which only ever names a position for a job that is selected or paused</remarks>
    public ValueTask SetPositionAsync(long position, CancellationToken cancellationToken)
        => RewindAsync(position, cancellationToken);

    /// <summary>
    /// Close the file and release what the stream held
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async ValueTask CloseAsync()
    {
        StopReading();
        await DrainAsync();

        _generation?.Dispose();
        _generation = null;
        _readTask = null;

        _codeProcessor.SetJobFile(Channel, null);
        File.Dispose();
    }

    /// <summary>
    /// Read and execute codes until the file runs out or the stream is frozen
    /// </summary>
    /// <param name="generation">Token cancelling this stretch of reading</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// The read-ahead window is what keeps the move queue full: codes are started without being
    /// waited for and drained one at a time, so the machine is never waiting for the file. What ends
    /// the loop is the file running out or a freeze, and which of them it was decides what the
    /// stream reports
    /// </remarks>
    private async Task ReadAheadAsync(CancellationToken generation)
    {
        Queue<Code> codes = new();
        Exception? failure = null;
        bool endOfFile = false;

        try
        {
            while (true)
            {
                // Fill the read-ahead window
                while (!_frozen && !endOfFile && _codePool.TryDequeue(out Code? sharedCode))
                {
                    Code? readCode = null;
                    try
                    {
                        try
                        {
                            readCode = await File.ReadCodeAsync(sharedCode, generation);
                            if (readCode is null)
                            {
                                _codePool.Enqueue(sharedCode);
                                endOfFile = true;
                                break;
                            }
                        }
                        catch
                        {
                            _codePool.Enqueue(sharedCode);
                            throw;
                        }

                        readCode.Flags |= CodeFlags.Asynchronous;
                        codes.Enqueue(readCode);

                        // The generation token goes into the execution, so that a freeze reaches the
                        // codes already read ahead: a job code blocked in a wait lets go of its
                        // channel instead of holding the pause up for as long as the wait would take
                        await readCode.ExecuteAsync(generation);
                    }
                    catch (OperationCanceledException)
                    {
                        // The freeze that cancelled the generation. What that means for the job is
                        // the controller's to settle
                    }
                    catch (Exception e)
                    {
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException!;
                        }
                        await _eventLogger.LogOutputAsync(MessageType.Error,
                                                          $"in job file (channel {Channel}) line {readCode?.LineNumber ?? File.LineNumber}: {e.Message}");
                        _logger.LogError(e, "Error in job file (channel {Channel}) line {LineNumber}: {Message}",
                                         Channel, readCode?.LineNumber ?? File.LineNumber, e.Message);
                        failure ??= e;
                    }
                }

                if (failure is not null)
                {
                    break;
                }

                // Drain one code, which lets the window refill behind it
                if (!codes.TryDequeue(out Code? code))
                {
                    break;
                }

                try
                {
                    try
                    {
                        // Logging of regular messages is done by the code itself
                        await code.Task;

                        // Comments are resolved internally and finish even when the stream is
                        // frozen, so they must not advance the position past the point the machine
                        // actually stopped at
                        if (!code.IsNonFirmwareComment)
                        {
                            Publish((code.FilePosition ?? 0L) + (code.Length ?? 0L));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The freeze, the file being exchanged, or an interceptor on an inactive
                        // channel. None of them is an error in the file
                    }
                    catch (Exception e)
                    {
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException!;
                        }
                        await _eventLogger.LogOutputAsync(MessageType.Error,
                                                          $"in job file (channel {Channel}) line {code.LineNumber ?? 0}: {e.Message}");
                        _logger.LogError(e, "Error in job file (channel {Channel}) line {LineNumber}: {Message}",
                                         Channel, code.LineNumber ?? 0, e.Message);
                        failure ??= e;
                    }
                }
                finally
                {
                    _codePool.Enqueue(code);
                }
            }
        }
        catch (Exception e)
        {
            // This task is owned by the controller, which learns what happened from the event below
            failure ??= e;
        }

        await ReportAsync(failure, endOfFile);
    }

    /// <summary>
    /// Say what became of the stretch of reading that has just ended
    /// </summary>
    /// <param name="failure">What went wrong, or null</param>
    /// <param name="endOfFile">Whether the file ran out of codes</param>
    /// <returns>Asynchronous task</returns>
    private async ValueTask ReportAsync(Exception? failure, bool endOfFile)
    {
        if (failure is not null)
        {
            await Post(new JobCommand.ReaderFailed(Index, failure));
            return;
        }

        if (_frozenAtBoundary)
        {
            // The boundary the pause was waiting for: the end of the job code that was running, and
            // of the macro it was inside. Reported whether or not the file also ran out
            await Post(new JobCommand.ReaderStopped(Index, Position));
            return;
        }

        if (!_frozen && endOfFile)
        {
            // The moves the file queued last have still to be made, so this says only that the file
            // ran out of codes: waiting for the machine is the controller's step.
            //
            // The flush comes first, because PurgeSyncRequestsFor clears the channel's job file and
            // a flush by file then finds no stack item and answers false without waiting for
            // anything. That is what the flush is for: a plugin may have inserted codes at the end
            // of the print file, and they are the job's as much as the file's own
            try
            {
                await _codeProcessor.FlushAsync(File);
            }
            catch (OperationCanceledException)
            {
                // Nothing left to flush
            }
            _codeProcessor.PurgeSyncRequestsFor(File);
            await Post(new JobCommand.ReaderFinished(Index));
        }

        // A freeze reports nothing here: the controller has the rewind point to send first
    }

    /// <summary>
    /// Record where the stream has got to
    /// </summary>
    /// <param name="position">Byte offset</param>
    private void Publish(long position) => _position = new ReaderPosition(position);

    /// <summary>
    /// Tell the controller what the stream did
    /// </summary>
    /// <param name="command">The event</param>
    /// <returns>Asynchronous task</returns>
    private async ValueTask Post(JobCommand command)
    {
        try
        {
            await _events.WriteAsync(command);
        }
        catch (ChannelClosedException)
        {
            // The controller has shut down, which is every stream's end
        }
    }
}
