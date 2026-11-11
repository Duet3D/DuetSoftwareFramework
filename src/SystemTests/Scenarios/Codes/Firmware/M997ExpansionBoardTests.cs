using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Firmware;

/// <summary>
/// <para>
/// Updating an expansion board over CAN: <c>M997 B</c>, the file the board then fetches a block at a
/// time, and what <c>boards[].state</c> and <c>state.status</c> say along the way.
/// </para>
/// <para>
/// The code is <c>ExpansionManager::UpdateRemoteFirmware</c> (ExpansionManager.cpp) and the serving
/// side is <c>HandleFirmwareBlockRequest</c> (CAN/CommandProcessor.cpp). The code only starts the
/// update: the board restarts into its bootloader and asks for the file, so the code has finished
/// long before the board has, and the object model is the only thing that says how it went.
/// </para>
/// </summary>
[TestFixture]
[GCode("M997")]
public class M997ExpansionBoardTests : BenchFixture
{
    /// <summary>CAN address of the board being updated</summary>
    private const byte Board = 3;

    /// <summary>Board type the scripted board reports</summary>
    private const string BoardType = "EXP3HC";

    /// <summary>Bootloader class the scripted board reports</summary>
    private const string BootloaderType = "SAME5x";

    /// <summary>What a board's firmware file is called, given <see cref="BoardType"/></summary>
    private const string FirmwareFile = "Duet3Firmware_EXP3HC.bin";

    /// <summary>What a board's bootloader file is called, given <see cref="BootloaderType"/></summary>
    private const string BootloaderFile = "Duet3Bootloader-SAME5x.bin";

    /// <summary>The most data one firmware block response carries</summary>
    private const int ChunkSize = 56;

    /// <summary>A firmware image whose every byte says where it came from</summary>
    private static byte[] Image(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

    /// <summary>
    /// A board that names its type and agrees to update, as Duet3Expansion does
    /// </summary>
    /// <param name="fake">The fake controller</param>
    /// <param name="boardType">Type it reports</param>
    /// <param name="extra">The <c>extra</c> byte of its board type answer, bit 0 of which says it takes a .uf2</param>
    private static void ScriptBoard(ScriptedCanMaster fake, string boardType = BoardType, byte extra = 0)
    {
        fake.OnCanMessage((ushort)CanMessageType.ReturnInfo, (controller, header, payload) =>
        {
            CanMessageReturnInfo info = CanMessageSerializer.Deserialize<CanMessageReturnInfo>(payload);
            bool isBootloader = info.Type == CanMessageReturnInfo.TypeBootloaderName;
            controller.InjectStandardReply(header, CodeResult.Ok, isBootloader ? BootloaderType : boardType,
                                           isBootloader ? (byte)0 : extra);
        });
        fake.ReportWith<CanMessageUpdateYourFirmware>(message => $"Board {message.BoardId} starting firmware update");
    }

    /// <summary>Start a bench with a running board at <see cref="Board"/></summary>
    /// <param name="prepareSd">Puts the firmware files on the card</param>
    /// <param name="prepareController">Scripts the board, <see cref="ScriptBoard"/> by default</param>
    private static async Task<Bench> StartAsync(Action<VirtualSd>? prepareSd = null,
                                                   Action<ScriptedCanMaster>? prepareController = null)
    {
        Bench bench = await Bench.With()
                                 .WithSd(prepareSd)
                                 .WithController(prepareController ?? (fake => ScriptBoard(fake)))
                                 .StartAsync();
        bench.CanMaster.InjectAnnounce(Board, BoardType, "3.7.0", "2026-09-08", numDrivers: 3);
        await WaitForBoardStateAsync(bench, BoardState.Running);
        bench.CanMaster.ClearCapture();
        return bench;
    }

    /// <summary>Wait for <c>boards[].state</c> of <see cref="Board"/> to say something</summary>
    private static Task WaitForBoardStateAsync(Bench bench, BoardState state)
        => bench.Host.WaitForModelAsync(model => model.FindBoard(Board)?.State, state, $"state of board {Board}");

    /// <summary>What the board has been sent in answer to its block requests, in order</summary>
    private static IReadOnlyList<CanMessageFirmwareUpdateResponse> Responses(Bench bench)
        => [.. bench.CanMaster.CanMessages<CanMessageFirmwareUpdateResponse>().Select(sent => sent.Message)];

    /// <summary>Wait for the given number of block responses to have been sent</summary>
    private static Task WaitForResponsesAsync(Bench bench, int count)
        => bench.CanMaster.WaitUntilAsync(() => Responses(bench).Count >= count, what: $"{count} firmware block responses");

    /// <summary>
    /// Put the board into the flashing state, as a successful M997 B does
    /// </summary>
    private static async Task StartUpdateAsync(Bench bench)
    {
        await bench.Host.ExecuteCodeAsync($"M997 B{Board}");
        await WaitForBoardStateAsync(bench, BoardState.Flashing);
        bench.CanMaster.ClearCapture();
    }

    /// <summary>
    /// M997 B asks the board what it is, checks there is a file for it and tells it to update
    /// </summary>
    /// <remarks>
    /// The board's type comes from asking it rather than from what it announced, as in
    /// RepRapFirmware, because this is the code that has to work on a board whose announcement is
    /// out of date. The address is sent twice, the second time inverted: a board ignores an update
    /// message that does not name it correctly both ways
    /// </remarks>
    [Test]
    public async Task M997BTellsTheBoardToUpdate()
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(100)));

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board}");

        (byte infoBoard, CanMessageReturnInfo info) = bench.CanMaster.LastCanMessage<CanMessageReturnInfo>();
        (byte updateBoard, CanMessageUpdateYourFirmware update) = bench.CanMaster.LastCanMessage<CanMessageUpdateYourFirmware>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.EqualTo($"Board {Board} starting firmware update"), "the board's own reply is the result");

            Assert.That(infoBoard, Is.EqualTo(Board));
            Assert.That(info.Type, Is.EqualTo(CanMessageReturnInfo.TypeBoardName));

            Assert.That(updateBoard, Is.EqualTo(Board));
            Assert.That(update.BoardId, Is.EqualTo(Board));
            Assert.That(update.InvertedBoardId, Is.EqualTo(unchecked((byte)~Board)));
            Assert.That(update.Module, Is.Zero, "FirmwareModule::main");
        });

        await WaitForBoardStateAsync(bench, BoardState.Flashing);
        await bench.Host.WaitForStatusAsync(MachineStatus.Updating);
    }

    /// <summary>
    /// M997 B0 is the main board, so it does not address a board at all
    /// </summary>
    /// <remarks>GCodes::UpdateFirmware compares B with the board's own address</remarks>
    [Test]
    public async Task M997B0IsNotARemoteUpdate()
    {
        await using Bench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M997 B0");

        Assert.Multiple(() =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageReturnInfo>(), Is.Empty);
            Assert.That(bench.CanMaster.CanMessages<CanMessageUpdateYourFirmware>(), Is.Empty);
        });
    }

    /// <summary>
    /// S3 updates the bootloader: the board is asked for its bootloader class instead of its type,
    /// and the file is named after that
    /// </summary>
    [Test]
    public async Task M997S3UpdatesTheBootloader()
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(BootloaderFile, Image(100)));

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board} S3");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"));
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageReturnInfo>().Message.Type,
                        Is.EqualTo(CanMessageReturnInfo.TypeBootloaderName));
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageUpdateYourFirmware>().Message.Module,
                        Is.EqualTo(3), "FirmwareModule::bootloader");
        });
    }

    /// <summary>
    /// A board that says it takes a .uf2 gets a .uf2, as does the Mini 5+, which predates being able
    /// to say so
    /// </summary>
    [TestCase("RPi_Pico", (byte)1, "Duet3Firmware_RPi_Pico.uf2")]
    [TestCase("Mini5plus", (byte)0, "Duet3Firmware_Mini5plus.uf2")]
    public async Task M997BLooksForTheBinaryTheBoardTakes(string boardType, byte extra, string file)
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(file, Image(100)),
                                                      fake => ScriptBoard(fake, boardType, extra));

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board}");

        Assert.That(reply, Does.Not.StartWith("Error:"), "the .uf2 is the file being looked for");
        Assert.That(bench.CanMaster.CanMessages<CanMessageUpdateYourFirmware>(), Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Only the main firmware and the bootloader of a board can be updated, and past the module
    /// numbers there are the value is refused as a value
    /// </summary>
    [TestCase("S1")]
    [TestCase("S2")]
    [TestCase("S5")]
    [TestCase("S-1")]
    public async Task M997BRefusesAModuleABoardDoesNotHave(string module)
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(100)));

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board} {module}");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain("Error: "));
            Assert.That(bench.CanMaster.CanMessages<CanMessageReturnInfo>(), Is.Empty, "refused before asking the board anything");
            Assert.That(bench.CanMaster.CanMessages<CanMessageUpdateYourFirmware>(), Is.Empty);
        });
    }

    /// <summary>An address no board can have is refused, not sent</summary>
    [Test]
    public async Task M997BRefusesAnAddressNoBoardCanHave()
    {
        await using Bench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{CanId.MaxCanAddress + 1}");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain("CAN address out of range"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageReturnInfo>(), Is.Empty);
        });
    }

    /// <summary>
    /// With no file for the board nothing is started, rather than restarting the board into its
    /// bootloader only to tell it there is nothing to fetch
    /// </summary>
    [Test]
    public async Task M997BDoesNotStartWithoutTheFile()
    {
        await using Bench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board}");

        BoardState? state = await bench.Host.ReadModelAsync(model => model.FindBoard(Board)?.State);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain($"Firmware file {FirmwareFile} not found"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageUpdateYourFirmware>(), Is.Empty);
            Assert.That(state, Is.EqualTo(BoardState.Running));
        });
    }

    /// <summary>A board that does not answer stops the update, and the timeout is the reason</summary>
    [Test]
    public async Task M997BReportsABoardThatDoesNotAnswer()
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(100)), fake =>
        {
            ScriptBoard(fake);
            fake.NeverAnswer<CanMessageReturnInfo>();
        });

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board}");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain($"CAN response timeout: board {Board}"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageUpdateYourFirmware>(), Is.Empty);
        });
    }

    /// <summary>
    /// A board that refuses to update stays what it was: flashing is only for a board that agreed,
    /// because the machine reports itself as updating until a flashing board comes back
    /// </summary>
    /// <remarks>
    /// The refusal is RepRapFirmware's when it runs as an expansion board, which cannot update its
    /// bootloader (InitiateFirmwareUpdate, CAN/CommandProcessor.cpp)
    /// </remarks>
    [Test]
    public async Task ABoardThatRefusesToUpdateIsNotFlashing()
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(BootloaderFile, Image(100)), fake =>
        {
            ScriptBoard(fake);
            fake.ReportWith<CanMessageUpdateYourFirmware>("unknown firmware module number", CodeResult.Error);
        });

        string reply = await bench.Host.ExecuteCodeAsync($"M997 B{Board} S3");

        BoardState? state = await bench.Host.ReadModelAsync(model => model.FindBoard(Board)?.State);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("unknown firmware module number"));
            Assert.That(state, Is.EqualTo(BoardState.Running));
        });
    }

    /// <summary>
    /// A board goes on reporting for a moment after agreeing to update, and that must not read as it
    /// being back
    /// </summary>
    /// <remarks>ExpansionManager::ProcessBoardStatusReport leaves a flashing board alone</remarks>
    [Test]
    public async Task AStatusReportDoesNotEndFlashing()
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(100)));
        await StartUpdateAsync(bench);

        const int marker = 12345;
        bench.CanMaster.InjectBoardStatus(Board, neverUsedRam: marker);
        await bench.Host.WaitForModelAsync(model => model.FindBoard(Board)?.FreeRam, marker, "the status report being applied");

        Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(Board)?.State), Is.EqualTo(BoardState.Flashing));
    }

    /// <summary>
    /// A request is answered in as many chunks as it takes, each saying where it starts, and the end
    /// of the file puts the board into the resetting state until it announces itself
    /// </summary>
    /// <remarks>
    /// Each response is transmitted only as far as its data. The board is told the length of the
    /// file, which is what it stops on, and a response goes out as a response rather than as a
    /// request the board would have to answer
    /// </remarks>
    [Test]
    public async Task AFileIsServedInChunksToItsEnd()
    {
        byte[] image = Image(2 * ChunkSize + 18);
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, image));
        await StartUpdateAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Updating);

        bench.CanMaster.InjectFirmwareBlockRequest(Board, BoardType, 0, 16384);

        await WaitForResponsesAsync(bench, 3);
        await WaitForBoardStateAsync(bench, BoardState.Resetting);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        IReadOnlyList<CanMessageFirmwareUpdateResponse> responses = Responses(bench);
        IReadOnlyList<(byte Board, byte[] Payload)> payloads = bench.CanMaster.CanPayloads<CanMessageFirmwareUpdateResponse>();
        Assert.That(responses, Has.Count.EqualTo(3), "nothing past the end of the file");
        Assert.Multiple(() =>
        {
            int[] lengths = [ChunkSize, ChunkSize, 18];
            for (int i = 0; i < responses.Count; i++)
            {
                CanMessageFirmwareUpdateResponse response = responses[i];
                int offset = i * ChunkSize;
                Assert.That(payloads[i].Board, Is.EqualTo(Board));
                Assert.That(response.Err, Is.EqualTo(CanMessageFirmwareUpdateResponse.ErrNone), $"chunk {i} error");
                Assert.That(response.FileOffset, Is.EqualTo(offset), $"chunk {i} offset");
                Assert.That(response.DataLength, Is.EqualTo(lengths[i]), $"chunk {i} length");
                Assert.That(response.FileLength, Is.EqualTo(image.Length), $"chunk {i} file length");
                Assert.That(payloads[i].Payload, Has.Length.EqualTo(8 + lengths[i]), $"chunk {i} on the wire");
                Assert.That(payloads[i].Payload.AsSpan(8).ToArray(), Is.EqualTo(image.AsSpan(offset, lengths[i]).ToArray()), $"chunk {i} data");
            }
        });

        (SendCanMessageHeader header, _) = bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage)
            .Select(packet => packet.DecodeCanMessage())
            .First(sent => sent.Header.MsgType == (ushort)CanMessageType.FirmwareBlockResponse);
        Assert.Multiple(() =>
        {
            Assert.That(header.Flags, Is.Not.Zero, "sent as a response");
            Assert.That(header.ReplyType, Is.EqualTo((ushort)CanMessageType.NoReply), "and nothing comes back for it");
        });

        bench.CanMaster.InjectAnnounce(Board, BoardType, "3.7.1", "2026-10-01", numDrivers: 3);
        await WaitForBoardStateAsync(bench, BoardState.Running);
        Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(Board)?.FirmwareVersion), Is.EqualTo("3.7.1"));
    }

    /// <summary>
    /// A file longer than one request stays flashing until the request that reaches its end, and each
    /// request starts where it says
    /// </summary>
    [Test]
    public async Task ABoardIsFlashingUntilItHasTheWholeFile()
    {
        byte[] image = Image(3 * ChunkSize);
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, image));
        await StartUpdateAsync(bench);

        bench.CanMaster.InjectFirmwareBlockRequest(Board, BoardType, 0, ChunkSize);
        await WaitForResponsesAsync(bench, 1);
        bench.CanMaster.InjectFirmwareBlockRequest(Board, BoardType, ChunkSize, ChunkSize);
        await WaitForResponsesAsync(bench, 2);

        Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(Board)?.State), Is.EqualTo(BoardState.Flashing));

        bench.CanMaster.InjectFirmwareBlockRequest(Board, BoardType, 2 * ChunkSize, 16384);
        await WaitForResponsesAsync(bench, 3);
        await WaitForBoardStateAsync(bench, BoardState.Resetting);

        IReadOnlyList<CanMessageFirmwareUpdateResponse> responses = Responses(bench);
        Assert.Multiple(() =>
        {
            Assert.That(responses.Select(response => (int)response.FileOffset), Is.EqualTo(new[] { 0, ChunkSize, 2 * ChunkSize }));
            Assert.That(bench.CanMaster.CanPayloads<CanMessageFirmwareUpdateResponse>()[1].Payload.AsSpan(8).ToArray(),
                        Is.EqualTo(image.AsSpan(ChunkSize, ChunkSize).ToArray()));
        });
    }

    /// <summary>
    /// The file is the one the board named, however it got there: its name comes from the request
    /// rather than from anything known about the board
    /// </summary>
    [TestCase(BootloaderType, (byte)3, false, BootloaderFile)]
    [TestCase("RPi_Pico", (byte)0, true, "Duet3Firmware_RPi_Pico.uf2")]
    public async Task TheFileIsTheOneTheRequestNames(string boardType, byte fileWanted, bool uf2, string file)
    {
        byte[] image = Image(10);
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(file, image));

        bench.CanMaster.InjectFirmwareBlockRequest(Board, boardType, 0, 16384, fileWanted, uf2);
        await WaitForResponsesAsync(bench, 1);

        CanMessageFirmwareUpdateResponse response = Responses(bench)[0];
        Assert.Multiple(() =>
        {
            Assert.That(response.Err, Is.EqualTo(CanMessageFirmwareUpdateResponse.ErrNone));
            Assert.That(response.FileLength, Is.EqualTo(image.Length));
        });
    }

    /// <summary>
    /// A board that asks for a file that is not there is told so, rather than left to time out, and
    /// the update has failed
    /// </summary>
    /// <remarks>
    /// The bootloader asks for the first block before it erases anything, so this is the answer that
    /// sends a board straight back to the firmware it already has
    /// </remarks>
    [Test]
    public async Task AMissingFileIsAFailedUpdate()
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(10)));
        await StartUpdateAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Updating);

        bench.CanMaster.InjectFirmwareBlockRequest(Board, "NOSUCHBOARD", 0, 16384);

        await WaitForResponsesAsync(bench, 1);
        await WaitForBoardStateAsync(bench, BoardState.FlashFailed);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        CanMessageFirmwareUpdateResponse response = Responses(bench)[0];
        Assert.Multiple(() =>
        {
            Assert.That(response.Err, Is.EqualTo(CanMessageFirmwareUpdateResponse.ErrNoFile));
            Assert.That(response.DataLength, Is.Zero);
            Assert.That(response.FileLength, Is.Zero);
            Assert.That(response.FileOffset, Is.Zero);
            Assert.That(CapturedLog.Dump(), Does.Contain("Received firmware update request for missing file 'Duet3Firmware_NOSUCHBOARD.bin'"));
        });
    }

    /// <summary>
    /// A request past the end of the file gets the one answer that says so
    /// </summary>
    /// <remarks>
    /// RepRapFirmware answers this twice, the second time with ErrNoFile and a report of a missing
    /// file, because it reaches its missing-file test with the request still unserved. The file is
    /// there, so this deliberately stops at the first answer; rrf-differences.md records it
    /// </remarks>
    [Test]
    public async Task AnOffsetPastTheEndIsAFailedUpdate()
    {
        byte[] image = Image(10);
        uint offset = (uint)image.Length;
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, image));
        await StartUpdateAsync(bench);

        bench.CanMaster.InjectFirmwareBlockRequest(Board, BoardType, offset, 16384);

        await WaitForResponsesAsync(bench, 1);
        await WaitForBoardStateAsync(bench, BoardState.FlashFailed);

        IReadOnlyList<CanMessageFirmwareUpdateResponse> responses = Responses(bench);
        Assert.That(responses, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(responses[0].Err, Is.EqualTo(CanMessageFirmwareUpdateResponse.ErrBadOffset));
            Assert.That(responses[0].DataLength, Is.Zero);
            Assert.That(responses[0].FileLength, Is.EqualTo(image.Length));
            Assert.That(responses[0].FileOffset, Is.Zero);
            Assert.That(CapturedLog.Dump(), Does.Contain($"Received firmware update request with bad file offset, actual {offset} max {image.Length}"));
        });
    }

    /// <summary>
    /// A request this side does not understand is refused. It says nothing about a running update,
    /// because nothing here knows what the board that sent it was doing
    /// </summary>
    [TestCase((byte)0, (byte)1, "Can't satisfy request for firmware file 1 from bootloader version 0")]
    [TestCase((byte)1, (byte)0, "Can't satisfy request for firmware file 0 from bootloader version 1")]
    public async Task ARequestThatIsNotUnderstoodIsRefused(byte bootloaderVersion, byte fileWanted, string message)
    {
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(10)));
        await StartUpdateAsync(bench);

        bench.CanMaster.InjectFirmwareBlockRequest(Board, BoardType, 0, 16384, fileWanted, bootloaderVersion: bootloaderVersion);
        await WaitForResponsesAsync(bench, 1);

        CanMessageFirmwareUpdateResponse response = Responses(bench)[0];
        BoardState? state = await bench.Host.ReadModelAsync(model => model.FindBoard(Board)?.State);
        Assert.Multiple(() =>
        {
            Assert.That(response.Err, Is.EqualTo(CanMessageFirmwareUpdateResponse.ErrOther));
            Assert.That(response.DataLength, Is.Zero);
            Assert.That(response.FileLength, Is.Zero);
            Assert.That(CapturedLog.Dump(), Does.Contain(message));
            Assert.That(state, Is.EqualTo(BoardState.Flashing));
        });
    }

    /// <summary>
    /// A board can ask for firmware nobody told it to fetch: one with no valid firmware does exactly
    /// that from its bootloader. It need not have announced itself first
    /// </summary>
    [Test]
    public async Task ABoardNothingKnowsAboutIsServed()
    {
        const byte unknownBoard = 42;
        await using Bench bench = await StartAsync(sd => sd.WriteFirmware(FirmwareFile, Image(10)));

        bench.CanMaster.InjectFirmwareBlockRequest(unknownBoard, BoardType, 0, 16384);

        await WaitForResponsesAsync(bench, 1);
        await bench.Host.WaitForModelAsync(model => model.FindBoard(unknownBoard)?.State, BoardState.Resetting, $"state of board {unknownBoard}");
    }
}
