using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol.CanMessages;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Link.Expansion;

/// <summary>
/// M569.5: the closed loop samples a board streams back, written to a CSV as they arrive
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's ClosedLoop (ClosedLoop.cpp). It is the only place a board sends a sustained
/// stream of unsolicited data: the command is answered as soon as the board accepts it, and the
/// samples arrive afterwards in <c>closedLoopData</c> messages of up to 56 data bytes each, packed
/// end to end in the order the filter bits select. Nothing interprets them here beyond turning them
/// into columns, because the analysis is the closed loop plugin's.
/// </para>
/// <para>
/// One collection runs at a time, machine-wide, as in RepRapFirmware: the file, the filter and the
/// sample number the next packet has to carry are one set of state, and a second collection would
/// interleave its samples into the first one's file
/// </para>
/// </remarks>
internal sealed class ClosedLoopDataCollector(FilePathResolver filePathResolver, Model.ObjectModel model,
                                              ILogger<ClosedLoopDataCollector> logger)
{
    /// <summary>Where the collected samples are written, as RepRapFirmware writes them</summary>
    private const string ClosedLoopDirectory = "0:/sys/closed-loop";

    /// <summary>
    /// The variables a board can be asked to record, in the order the filter bits select them, with
    /// the heading each gets in the file
    /// </summary>
    /// <remarks>
    /// The bit numbers are CANlib's <c>CL_RECORD_</c> constants (Duet3Common.h) and the widths are its
    /// <c>ClosedLoopSampleLength</c> table beside them. The headings are the order RepRapFirmware
    /// writes them in, which is not the order of the bits: the PID V and A terms were added after the
    /// phase and current variables and took the bits above them, but they are written where the rest
    /// of the PID terms are
    /// </remarks>
    private static readonly (ushort Bit, string Heading, SampleFormat Format)[] Variables =
    [
        (1 << 0, "Raw Encoder Reading", SampleFormat.Int32),
        (1 << 1, "Measured Motor Steps", SampleFormat.Float32),
        (1 << 2, "Target Motor Steps", SampleFormat.Float32),
        (1 << 3, "Current Error", SampleFormat.Float32),
        (1 << 4, "PID Control Signal", SampleFormat.Float16),
        (1 << 5, "PID P Term", SampleFormat.Float16),
        (1 << 6, "PID I Term", SampleFormat.Float16),
        (1 << 7, "PID D Term", SampleFormat.Float16),
        (1 << 13, "PID V Term", SampleFormat.Float16),
        (1 << 14, "PID A Term", SampleFormat.Float16),
        (1 << 8, "Measured Step Phase", SampleFormat.UInt16),
        (1 << 9, "Desired Step Phase", SampleFormat.UInt16),
        (1 << 10, "Phase Shift", SampleFormat.UInt16),
        (1 << 11, "Coil A Current", SampleFormat.Int16),
        (1 << 12, "Coil B Current", SampleFormat.Int16)
    ];

    /// <summary>
    /// How one recorded variable is laid out in a sample, and how it is written back out
    /// </summary>
    /// <remarks>
    /// The widths are CANlib's <c>ClosedLoopSampleLength</c> table, but the width alone does not say
    /// how to read a value: the phase shift is sixteen bits like the PID terms beside it and is an
    /// integer, which is how the board writes it (Duet3Expansion ClosedLoop.cpp:931 <c>PutU16</c>) and
    /// how RepRapFirmware reads it back (ClosedLoop.cpp:250 <c>FetchLEU16</c>)
    /// </remarks>
    private enum SampleFormat
    {
        /// <summary>A signed 32-bit count, written as an integer</summary>
        Int32,

        /// <summary>A single precision value, written to two decimal places</summary>
        Float32,

        /// <summary>A half precision value, written to one decimal place</summary>
        Float16,

        /// <summary>An unsigned 16-bit value, written as an integer</summary>
        UInt16,

        /// <summary>A signed 16-bit value, written as an integer</summary>
        Int16
    }

    /// <summary>How many bytes one variable takes in a sample</summary>
    private static int Width(SampleFormat format) => format switch
    {
        SampleFormat.Int32 or SampleFormat.Float32 => 4,
        _ => 2
    };

    /// <summary>The state of the collection in progress, or null when none is</summary>
    private sealed record Collection(StreamWriter Writer, string VirtualPath, ushort Filter, byte Board, int Requested)
    {
        /// <summary>The variables this run records, worked out once rather than per sample</summary>
        public (ushort Bit, string Heading, SampleFormat Format)[] Recording { get; } = Selected(Filter);

        /// <summary>How many bytes one of its samples takes, including the time stamp</summary>
        public int SampleLength { get; } = sizeof(float) + Selected(Filter).Sum(variable => Width(variable.Format));

        /// <summary>Sample number the next packet has to start at</summary>
        public int ExpectedSample { get; set; }

        /// <summary>When the last packet arrived, which is what tells a stalled run from a slow one</summary>
        public DateTime LastPacket { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// How long a collection may go without a packet before a later M569.5 may close it
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>DataReceiveTimeout</c> (ClosedLoop.cpp:132). Without it a board that
    /// stopped mid-stream would hold the file open and refuse every later collection, with nothing
    /// short of a restart to clear it
    /// </remarks>
    private static readonly TimeSpan DataReceiveTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The most samples one run may ask for, which is what bounds the file and the board's buffer
    /// </summary>
    /// <remarks>CANlib's <c>MaxSamples</c>, the bound RepRapFirmware reads S against (ClosedLoop.cpp:112)</remarks>
    private const int MaxSamples = 65535;

    /// <summary>
    /// The highest collection mode A may name: 0 records the drive as it runs, 1 runs the board's own
    /// manoeuvre first
    /// </summary>
    private const int MaxMode = 1;

    /// <summary>
    /// Stands in <c>_collection</c> between claiming a collection and having a file to record it in
    /// </summary>
    /// <remarks>
    /// The claim has to be taken before the file is opened, or two codes arriving together both get
    /// past the test; it has to be given up again if the file never opens, which is what
    /// <see cref="Release" /> is for
    /// </remarks>
    private static readonly Collection Claimed = new(StreamWriter.Null, string.Empty, 0, 0, 0);

    private readonly Lock _lock = new();
    private Collection? _collection;

    /// <summary>
    /// M569.5: start collecting, or report on the collection in progress
    /// </summary>
    /// <param name="driver">The driver named by P</param>
    /// <param name="code">The code</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Without S this is a request for the status, which is a warning when nothing is being collected
    /// - the closed loop plugin reads the severity (RRF ClosedLoop.cpp:119)
    /// </remarks>
    public async ValueTask<Message> StartAsync(DriverId driver, Commands.Code code, LinkInterface linkInterface,
                                               CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('S', out int samples, min: 0, max: MaxSamples))
        {
            lock (_lock)
            {
                return (_collection is null)
                    ? new Message(MessageType.Warning, "Closed loop data is not being collected")
                    : new Message(MessageType.Success,
                                  string.Create(CultureInfo.InvariantCulture,
                                                $"Collecting sample: {_collection.ExpectedSample}/{_collection.Requested}"));
            }
        }

        // The limits are the accessors' to enforce, which refuses a value out of range where it
        // stands and in RepRapFirmware's wording. D and V carry no limit of their own there either
        int mode = code.GetInt('A', defaultValue: 0, min: 0, max: MaxMode);
        int filter = code.GetInt('D', defaultValue: 0);
        int rate = code.GetInt('R', defaultValue: 0, min: 0, max: ushort.MaxValue);
        int movement = code.GetInt('V', defaultValue: 0);

        // Claimed before the file is opened, and in one step, so two codes arriving together cannot
        // both get past the test and leave one of their writers with nothing holding it
        bool stalled = false;
        lock (_lock)
        {
            if (_collection is not null)
            {
                // A collection whose board stopped sending is not one in progress, and saying so is
                // the only way back without a restart (RRF ClosedLoop.cpp:129-142)
                if (ReferenceEquals(_collection, Claimed)
                    || DateTime.UtcNow - _collection.LastPacket < DataReceiveTimeout)
                {
                    return new Message(MessageType.Error, "Closed loop data is already being collected");
                }
                stalled = true;
            }
            else
            {
                _collection = Claimed;
            }
        }

        if (stalled)
        {
            await CloseAsync(deleteFile: false, cancellationToken);
            return new Message(MessageType.Error, "Closed loop data collection timed out, closing file");
        }

        // Named by the board and the moment when F does not say, so two runs never collide
        string name = code.TryGetString('F', out string? given) && !string.IsNullOrWhiteSpace(given)
            ? given
            : string.Create(CultureInfo.InvariantCulture,
                            $"{driver.Board}_{DateTime.UtcNow:yyyy-MM-dd_HH.mm.ss}.csv");
        string virtualPath = $"{ClosedLoopDirectory}/{name}";

        StreamWriter writer;
        try
        {
            string physicalPath = await filePathResolver.ToPhysicalAsync(virtualPath, cancellationToken: cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
            writer = new StreamWriter(physicalPath, append: false, Encoding.UTF8);
            await writer.WriteLineAsync(HeaderLine((ushort)filter));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Failed to create closed loop data file {Path}", virtualPath);
            Release();
            return new Message(MessageType.Error, "failed to create data collection file");
        }

        if (samples == 0)
        {
            // Nothing to wait for, so the file is finished the moment it was opened
            await writer.DisposeAsync();
            Release();
            return new Message(MessageType.Warning, "no samples recorded");
        }

        lock (_lock)
        {
            _collection = new Collection(writer, virtualPath, (ushort)filter, (byte)driver.Board, samples);
        }

        CanMessageStartClosedLoopDataCollection request = new()
        {
            DeviceNumber = (byte)driver.Port,
            Filter = (ushort)filter,
            Mode = (byte)mode,
            Movement = (byte)movement,
            NumSamples = (ushort)samples,
            Rate = (ushort)rate
        };
        Message reply = await linkInterface.SendCanRequestAsync((byte)driver.Board, in request, cancellationToken);
        if (!reply.Succeeded())
        {
            // The board never started, so the file would only ever hold its header. A board that did
            // not answer at all counts: leaving the claim standing would refuse every later M569.5
            // with "already being collected" and leave this file open behind it
            await CloseAsync(deleteFile: true, cancellationToken);
        }
        return reply;
    }

    /// <summary>Give up a claim that never became a collection</summary>
    private void Release()
    {
        lock (_lock)
        {
            if (ReferenceEquals(_collection, Claimed))
            {
                _collection = null;
            }
        }
    }

    /// <summary>
    /// Write the samples one packet carries, and close the file when the board says it was the last
    /// </summary>
    /// <param name="source">CAN address the packet came from</param>
    /// <param name="data">The packet</param>
    /// <param name="payload">Its raw bytes, which is where the samples are</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>RepRapFirmware's ClosedLoop::ProcessReceivedData (ClosedLoop.cpp:213)</remarks>
    public async ValueTask ProcessDataAsync(byte source, CanMessageClosedLoopData data, byte[] payload,
                                            CancellationToken cancellationToken)
    {
        Collection collection;
        lock (_lock)
        {
            if (_collection is null || ReferenceEquals(_collection, Claimed))
            {
                return;
            }
            collection = _collection;
            collection.LastPacket = DateTime.UtcNow;
        }

        // A packet that does not carry on from the last one means samples went missing, and a packet
        // too short for the samples it claims cannot be read at all. Either way what is in the file
        // is no longer one run, so it says so and stops
        int headerLength = (int)data.GetActualDataLength(0);
        ReadOnlyMemory<byte> samples = payload.AsMemory(Math.Min(headerLength, payload.Length));
        int bytesPerSample = collection.SampleLength;
        if (data.FirstSampleNumber != collection.ExpectedSample)
        {
            await collection.Writer.WriteLineAsync("Data lost");
            await CloseAsync(deleteFile: false, cancellationToken);
            return;
        }
        if (bytesPerSample * data.NumSamples > samples.Length)
        {
            await collection.Writer.WriteLineAsync("Bad data received");
            await CloseAsync(deleteFile: false, cancellationToken);
            return;
        }

        // One builder for the packet rather than one per sample, and one flush at the end rather than
        // one await per line: this runs on the report channel every board shares, and a collection at
        // full rate is twenty packets a second
        StringBuilder line = new();
        int offset = 0;
        for (int sample = 0; sample < data.NumSamples; sample++)
        {
            line.Clear();
            line.Append(CultureInfo.InvariantCulture, $"{collection.ExpectedSample},");

            // Every sample opens with its own time stamp, which is not one of the filtered variables
            line.Append(ReadValue(samples.Span, ref offset, SampleFormat.Float32));
            foreach ((_, _, SampleFormat format) in collection.Recording)
            {
                line.Append(',').Append(ReadValue(samples.Span, ref offset, format));
            }

            line.Append('\n');
            collection.Writer.Write(line);
            collection.ExpectedSample++;
        }

        await collection.Writer.FlushAsync(cancellationToken);

        if (data.LastPacket)
        {
            if (data.Overflowed)
            {
                await collection.Writer.WriteLineAsync("Buffer overflowed");
            }
            if (data.BadSample)
            {
                await collection.Writer.WriteLineAsync("Data contains bad sample(s)");
            }
            await CloseAsync(deleteFile: false, cancellationToken);
        }
        else
        {
            logger.LogDebug("Collected {Samples} closed loop samples from board {Source}", data.NumSamples, source);
        }
    }

    /// <summary>
    /// Finish the collection in progress, and record the run against the board it came from
    /// </summary>
    /// <param name="deleteFile">Whether to throw the file away, for a collection that never started</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <c>boards[].closedLoop</c> is what a client watches to know a run finished and how much of it
    /// arrived, which is why the count is the samples received rather than the samples asked for
    /// (RRF ExpansionManager::AddClosedLoopRun)
    /// </remarks>
    private async ValueTask CloseAsync(bool deleteFile, CancellationToken cancellationToken)
    {
        Collection? finished;
        lock (_lock)
        {
            finished = _collection;
            _collection = null;
        }
        if (finished is null)
        {
            return;
        }

        await finished.Writer.DisposeAsync();
        if (finished.VirtualPath.Length == 0)
        {
            // A claim that never became a collection has no file and no run to record
            return;
        }
        if (deleteFile)
        {
            try
            {
                File.Delete(await filePathResolver.ToPhysicalAsync(finished.VirtualPath, cancellationToken: cancellationToken));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Failed to delete closed loop data file {Path}", finished.VirtualPath);
            }
            return;
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Board board = model.GetOrCreateBoard(finished.Board);
            board.ClosedLoop ??= new BoardClosedLoop();
            board.ClosedLoop.Points = finished.ExpectedSample;
            board.ClosedLoop.Runs++;
        }
    }

    /// <summary>
    /// The variables a filter selects, in the order the samples carry them
    /// </summary>
    /// <param name="filter">Bitmap of the variables being recorded</param>
    /// <remarks>
    /// The one place the bitmap is read. The heading line, the length of a sample and the walk across
    /// one all ask the same question, and asking it three ways is how they come to disagree
    /// </remarks>
    private static (ushort Bit, string Heading, SampleFormat Format)[] Selected(ushort filter)
        => [.. Variables.Where(variable => (filter & variable.Bit) != 0)];

    /// <summary>The heading line a file opens with, naming the variables that were asked for</summary>
    /// <param name="filter">Bitmap of the variables being recorded</param>
    private static string HeaderLine(ushort filter)
        => "Sample,Timestamp," + string.Join(',', Selected(filter).Select(variable => variable.Heading));

    /// <summary>Read one recorded value out of a sample and write it the way the file holds it</summary>
    /// <param name="samples">The packet's sample bytes</param>
    /// <param name="offset">Where this value starts, advanced past it</param>
    /// <param name="format">How it is laid out</param>
    private static string ReadValue(ReadOnlySpan<byte> samples, ref int offset, SampleFormat format)
    {
        ReadOnlySpan<byte> value = samples[offset..];
        offset += Width(format);
        return format switch
        {
            SampleFormat.Int32 => BinaryPrimitives.ReadInt32LittleEndian(value).ToString(CultureInfo.InvariantCulture),
            SampleFormat.Float32 => BinaryPrimitives.ReadSingleLittleEndian(value).ToString("F2", CultureInfo.InvariantCulture),
            SampleFormat.Float16 => ((float)BinaryPrimitives.ReadHalfLittleEndian(value)).ToString("F1", CultureInfo.InvariantCulture),
            SampleFormat.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(value).ToString(CultureInfo.InvariantCulture),
            _ => BinaryPrimitives.ReadInt16LittleEndian(value).ToString(CultureInfo.InvariantCulture)
        };
    }
}
