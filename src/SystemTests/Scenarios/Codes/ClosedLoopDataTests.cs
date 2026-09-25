using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M569.5, closed loop data collection: the request that starts a board streaming samples, the CSV
/// the samples are written to as they arrive, and what a bare form reports while it is happening
/// </summary>
/// <remarks>
/// The bench half of <c>lib/DuetRegressionTesting/testcases/drivers/m569-5-closed-loop-data-collection.yaml</c>.
/// It is the only place in the suite where a board sends a sustained stream of unsolicited data, so
/// the scenario drives both halves: the command, which is answered as soon as the board accepts it,
/// and the <c>closedLoopData</c> packets that follow it
/// </remarks>
[TestFixture]
public class ClosedLoopDataTests : BenchFixture
{
    /// <summary>
    /// The filter <c>m569-5-closed-loop-data-collection.yaml</c> collects with: the raw encoder
    /// reading, the measured motor steps and the current error
    /// </summary>
    /// <remarks>
    /// CANlib's CL_RECORD_RAW_ENCODER_READING, CL_RECORD_CURRENT_MOTOR_STEPS and
    /// CL_RECORD_CURRENT_ERROR (Duet3Common.h), which are bits 0, 1 and 3
    /// </remarks>
    private const int Filter = 0b1011;

    /// <summary>
    /// A bare M569.5 P is a status request, and says so as a warning while nothing is being collected
    /// </summary>
    /// <remarks>
    /// RRF ClosedLoop.cpp:119 notes that the closed loop plugin relies on the severity being a
    /// warning, which is why the reference records one
    /// </remarks>
    [Test]
    public async Task M569Point5ReportsThatNothingIsBeingCollected()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.5 P{DriversBench.ClosedLoopBoard}.0")).TrimEnd(),
                    Is.EqualTo("Warning: M569.5: Closed loop data is not being collected"));
    }

    /// <summary>
    /// M569.5 with S asks the board to collect, and the parameters reach it in their own message
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-5-closed-loop-data-collection.yaml</c>: S is the number of samples,
    /// A the collection mode, R the rate, D the variables to record and V the movement to make
    /// </remarks>
    [Test]
    public async Task M569Point5AsksTheBoardToCollect()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync(
            $"M569.5 P{DriversBench.ClosedLoopBoard}.0 F\"drt-m569-5-data.csv\" S200 A0 R100 D{Filter} V0");
        Assert.That(reply.Trim(), Is.Empty, "the board took the request");

        (byte board, CanMessageStartClosedLoopDataCollection sent) =
            bench.CanMaster.LastCanMessage<CanMessageStartClosedLoopDataCollection>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversBench.ClosedLoopBoard), "the driver's board is asked");
            Assert.That(sent.DeviceNumber, Is.EqualTo(0), "for its own driver number");
            Assert.That(sent.NumSamples, Is.EqualTo(200));
            Assert.That(sent.Mode, Is.EqualTo(0));
            Assert.That(sent.Rate, Is.EqualTo(100));
            Assert.That(sent.Filter, Is.EqualTo(Filter));
            Assert.That(sent.Movement, Is.EqualTo(0));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.5 P{DriversBench.ClosedLoopBoard}.0")).TrimEnd(),
                    Is.EqualTo("Collecting sample: 0/200"),
                    "and while it is collecting, the bare form says how far it has got");
    }

    /// <summary>
    /// The samples the board streams back are written to the file, one line each, under the headings
    /// the filter asked for, and the run is recorded against the board when the last packet arrives
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m569-5-closed-loop-data-collection.yaml</c>, whose delta is
    /// <c>boards[2].closedLoop.points 0 -&gt; 200</c> and <c>runs 0 -&gt; 1</c>. The count is the
    /// samples that arrived rather than the samples asked for, which is what makes a run that stopped
    /// early tell a client so (RRF ExpansionManager::AddClosedLoopRun)
    /// </remarks>
    [Test]
    public async Task M569Point5WritesTheSamplesItIsSent()
    {
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.ClosedLoopBoard;

        await bench.Host.ExecuteCodeAsync(
            $"M569.5 P{board}.0 F\"drt-m569-5-data.csv\" S4 A0 R100 D{Filter} V0");

        // Two packets of two samples, as a board sends them: as many as fit, then the rest
        bench.CanMaster.InjectClosedLoopData(board, firstSample: 0, filter: Filter,
                                             samples: [(0.0f, 100, 1.5f, 0.25f), (0.01f, 101, 1.6f, -0.25f)],
                                             lastPacket: false);
        bench.CanMaster.InjectClosedLoopData(board, firstSample: 2, filter: Filter,
                                             samples: [(0.02f, 102, 1.7f, 0.0f), (0.03f, 103, 1.8f, 0.5f)],
                                             lastPacket: true);

        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.ReadModelAsync(model => model.Boards[board].ClosedLoop?.Runs ?? 0).Result == 1,
            what: "the collected run being recorded");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].ClosedLoop!.Points), Is.EqualTo(4),
                        "boards[].closedLoop.points is how many samples arrived, not how many were asked for");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Boards[board].ClosedLoop!.Runs), Is.EqualTo(1),
                        "and runs counts the collection that finished");
        });

        string[] lines = File.ReadAllLines(Path.Combine(bench.Host.Sd.Root, "sys", "closed-loop", "drt-m569-5-data.csv"));
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("Sample,Timestamp,Raw Encoder Reading,Measured Motor Steps,Current Error"),
                        "the heading names the variables the filter selected, in the order they are packed");
            Assert.That(lines[1], Is.EqualTo("0,0.00,100,1.50,0.25"));
            Assert.That(lines[4], Is.EqualTo("3,0.03,103,1.80,0.50"));
            Assert.That(lines, Has.Length.EqualTo(5), "a heading and one line per sample");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.5 P{board}.0")).TrimEnd(),
                    Is.EqualTo("Warning: M569.5: Closed loop data is not being collected"),
                    "and the collection is over, so the bare form says so again");
    }

    /// <summary>
    /// A packet that does not carry on from the last one means samples went missing, which the file
    /// records before the collection stops
    /// </summary>
    /// <remarks>
    /// RRF ClosedLoop::ProcessReceivedData: what is in the file is no longer one run, so it says so
    /// rather than leaving a gap for a reader to find
    /// </remarks>
    [Test]
    public async Task M569Point5RecordsLostSamples()
    {
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.ClosedLoopBoard;

        await bench.Host.ExecuteCodeAsync($"M569.5 P{board}.0 F\"lost.csv\" S4 A0 R100 D{Filter} V0");
        bench.CanMaster.InjectClosedLoopData(board, firstSample: 0, filter: Filter,
                                             samples: [(0.0f, 100, 1.5f, 0.25f)], lastPacket: false);
        bench.CanMaster.InjectClosedLoopData(board, firstSample: 3, filter: Filter,
                                             samples: [(0.03f, 103, 1.8f, 0.5f)], lastPacket: false);

        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.ReadModelAsync(model => model.Boards[board].ClosedLoop?.Runs ?? 0).Result == 1,
            what: "the interrupted run being recorded");

        string[] lines = File.ReadAllLines(Path.Combine(bench.Host.Sd.Root, "sys", "closed-loop", "lost.csv"));
        Assert.Multiple(() =>
        {
            Assert.That(lines[^1], Is.EqualTo("Data lost"));
            Assert.That(lines, Has.Length.EqualTo(3), "the heading, the one sample that arrived, and the note");
        });
    }

    /// <summary>
    /// A second collection while one is running is refused, so the samples of two runs never end up
    /// interleaved in one file
    /// </summary>
    /// <remarks>RRF ClosedLoop.cpp:139</remarks>
    [Test]
    public async Task M569Point5RefusesASecondCollection()
    {
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.ClosedLoopBoard;

        await bench.Host.ExecuteCodeAsync($"M569.5 P{board}.0 F\"first.csv\" S4 A0 R100 D{Filter} V0");
        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.5 P{board}.0 F\"second.csv\" S4 A0 R100 D{Filter} V0")).TrimEnd(),
                    Is.EqualTo("Error: M569.5: Closed loop data is already being collected"));
    }

    /// <summary>
    /// A board that never answers the request leaves nothing behind, so the next collection can start
    /// </summary>
    /// <remarks>
    /// The claim on the collection has to be given up whether the board refused or simply did not
    /// answer: a timeout is not an error reply, and treating it as success would hold the file open
    /// and refuse every later M569.5 with "already being collected" until a restart
    /// </remarks>
    [Test]
    public async Task M569Point5ReleasesTheCollectionWhenTheBoardDoesNotAnswer()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.NeverAnswer<CanMessageStartClosedLoopDataCollection>());
        const byte board = DriversBench.ClosedLoopBoard;

        string reply = await bench.Host.ExecuteCodeAsync($"M569.5 P{board}.0 F\"unanswered.csv\" S4 A0 R100 D{Filter} V0");
        Assert.That(reply, Does.Contain("CAN response timeout"), "the board is reported as not having answered");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.5 P{board}.0")).TrimEnd(),
                    Is.EqualTo("Warning: M569.5: Closed loop data is not being collected"),
                    "and nothing is left collecting, so the next one can start");
    }

    /// <summary>
    /// A sample count or rate outside what the message can carry is refused rather than wrapped
    /// </summary>
    /// <remarks>
    /// RepRapFirmware reads both with <c>TryGetLimitedUIValue</c> (ClosedLoop.cpp:112 and :150).
    /// Casting instead would make S70000 collect 4464 samples and S-1 collect 65535, neither of them
    /// what was asked for and neither of them reported. The column is quoted rather than stated,
    /// because it is where the value stands in a line the case builds
    /// </remarks>
    [TestCase("S70000", "parameter 'S' too high")]
    [TestCase("S-1", "parameter 'S' too low")]
    [TestCase("S4 R70000", "parameter 'R' too high")]
    public async Task M569Point5RefusesAValueItCannotCarry(string parameters, string expected)
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync($"M569.5 P{DriversBench.ClosedLoopBoard}.0 {parameters} D{Filter}")
                             .Result.TrimEnd(),
                        Does.StartWith("Error: at column ").And.EndWith($"M569.5: {expected}"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageStartClosedLoopDataCollection>(), Is.Empty);
        });
    }

    /// <summary>
    /// S0 asks for no samples at all, which is a file with nothing in it and nothing asked of the board
    /// </summary>
    /// <remarks>RRF ClosedLoop.cpp:196</remarks>
    [Test]
    public async Task M569Point5WithNoSamplesRecordsNothing()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync($"M569.5 P{DriversBench.ClosedLoopBoard}.0 F\"empty.csv\" S0 D{Filter}")
                             .Result.TrimEnd(),
                        Is.EqualTo("Warning: M569.5: no samples recorded"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageStartClosedLoopDataCollection>(), Is.Empty,
                        "the board is never asked for a collection there is nothing to collect");
        });
    }
}

/// <summary>
/// Sending closed loop samples as a board sends them, which nothing else in the suite needs
/// </summary>
internal static class ClosedLoopDataInjection
{
    /// <summary>
    /// Stream one packet of closed loop samples from a board
    /// </summary>
    /// <param name="canMaster">The fake controller</param>
    /// <param name="board">CAN address of the board collecting</param>
    /// <param name="firstSample">Sample number the packet starts at</param>
    /// <param name="filter">Bitmap of the variables it carries, which decides how they are packed</param>
    /// <param name="samples">The samples: a time stamp, an encoder count, the motor steps and the error</param>
    /// <param name="lastPacket">Whether the collection ends with this packet</param>
    /// <remarks>
    /// Packed the way the board packs them - a time stamp, then each selected variable in filter bit
    /// order - because what the collector has to get right is reading them back out
    /// </remarks>
    public static void InjectClosedLoopData(this ScriptedCanMaster canMaster, byte board, int firstSample, int filter,
                                            (float Timestamp, int Encoder, float Steps, float Error)[] samples,
                                            bool lastPacket)
    {
        CanMessageClosedLoopData data = default;
        data.FirstSampleNumber = (uint)firstSample;
        data.Filter = (ushort)filter;
        data.NumSamples = (byte)samples.Length;
        data.LastPacket = lastPacket;

        // The header is the leading eight bytes of a struct that spans the whole frame, so it is
        // written into a full-sized scratch and only its header copied out
        byte[] header = new byte[64];
        System.Runtime.InteropServices.MemoryMarshal.Write(header, in data);
        byte[] payload = new byte[8 + (samples.Length * 16)];
        header.AsSpan(0, 8).CopyTo(payload);
        int offset = 8;
        foreach ((float timestamp, int encoder, float steps, float error) in samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset), timestamp);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4), encoder);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 8), steps);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 12), error);
            offset += 16;
        }

        canMaster.InjectCanResponse(DuetControlServer.Link.LinkInterface.UnsolicitedTxToken,
                                    (ushort)CanMessageType.ClosedLoopData, board, payload);
    }
}
