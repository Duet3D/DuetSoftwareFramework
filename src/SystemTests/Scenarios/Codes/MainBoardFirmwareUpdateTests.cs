using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// <para>
/// Updating the main board: <c>M997</c> or <c>M997 B0</c>. DuetCANMaster cannot write its own flash
/// while it runs from it, so the in-application programmer goes over the link into RAM a segment at a
/// time, <c>StartIap</c> starts it, and the firmware follows as bare segments that the IAP writes and
/// then checks against the length and CRC16 it is given.
/// </para>
/// <para>
/// The sequence is <c>SbcInterface::ReceiveAndStartIap</c> and <c>RepRap::StartIap</c> on the controller
/// and <c>LinkService::PerformFirmwareUpdate</c> in the native library. The scripted controller
/// records the segments rather than flashing them, so these scenarios pin what is sent, not whether
/// the IAP accepts it.
/// </para>
/// </summary>
[TestFixture]
public class MainBoardFirmwareUpdateTests : SystemTests.Host.BenchFixture
{
    /// <summary>What the controller says its firmware file is called</summary>
    private const string FirmwareFile = "DuetCANMaster_MB6HC.bin";

    /// <summary>What the controller says its IAP is called</summary>
    private const string IapFile = "Duet3_SBCiap32_MB6HC.bin";

    /// <summary>The most IAP data one <c>WriteIap</c> packet carries</summary>
    private const int IapSegmentSize = 1536;

    /// <summary>The size of a firmware segment, which is also what the IAP writes at a time</summary>
    private const int FirmwareSegmentSize = 2048;

    /// <summary>What a failed update tells the user to do</summary>
    private const string GaveUp = "Could not update firmware after 3 attempts";

    /// <summary>An image whose every byte says where it came from</summary>
    private static byte[] Image(int length, byte seed = 0)
        => [.. Enumerable.Range(0, length).Select(i => (byte)((i + seed) % 251))];

    /// <summary>The IAP the scenarios load, which takes more than one packet</summary>
    private static readonly byte[] Iap = Image(3000);

    /// <summary>The firmware the scenarios flash, which does not fill its last segment</summary>
    private static readonly byte[] Firmware = Image(5000, seed: 100);

    /// <summary>Put both binaries in 0:/firmware</summary>
    private static void WriteBinaries(VirtualSd sd)
    {
        sd.WriteFirmware(IapFile, Iap);
        sd.WriteFirmware(FirmwareFile, Firmware);
    }

    /// <summary>Start a bench whose controller has named its firmware files</summary>
    /// <param name="prepareSd">Puts the binaries on the card, both by default</param>
    /// <param name="firmwareFile">Firmware file the controller reports</param>
    /// <param name="iapFile">IAP file the controller reports</param>
    private static async Task<JobBench> StartAsync(Action<VirtualSd>? prepareSd = null, string firmwareFile = FirmwareFile,
                                                   string iapFile = IapFile)
    {
        JobBench bench = await JobControlBench.StartAsync(prepareSd: prepareSd ?? WriteBinaries);
        bench.CanMaster.InjectControllerBoardInfo("Duet 3 MB6HC", "MB6HC", "RepRapFirmware for Duet 3 MB6HC", "4.0.0",
                                                  "2026-11-05", firmwareFile, iapFile);
        await bench.Host.WaitForModelAsync(model => model.Boards[0].ShortName, "MB6HC", "the controller's identity");
        bench.CanMaster.ClearCapture();
        return bench;
    }

    /// <summary>The bytes of a capture of <c>WriteIap</c> packets, in the order they were sent</summary>
    private static byte[] IapSent(JobBench bench)
        => [.. bench.CanMaster.SbcPackets(SbcRequest.WriteIap).SelectMany(packet => packet.Data)];

    /// <summary>The firmware as the IAP sees it: whole segments, the last one filled with 0xFF</summary>
    private static byte[] Padded(byte[] image)
        => [.. image, .. Enumerable.Repeat((byte)0xFF, -image.Length & (FirmwareSegmentSize - 1))];

    /// <summary>Assert that the controller has been sent nothing that would start an update</summary>
    private static void AssertNothingSent(JobBench bench)
    {
        Assert.Multiple(() =>
        {
            Assert.That(IapSent(bench), Is.Empty, "the running firmware is not disturbed");
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.StartIap), Is.Empty);
            Assert.That(bench.CanMaster.FlashedSegments, Is.Empty);
            Assert.That(bench.Host.Model.State.Status, Is.Not.EqualTo(MachineStatus.Updating));
        });
    }

    /// <summary>
    /// Every way of naming the main board loads the IAP, starts it, and sends it the firmware and the
    /// checksum to verify that firmware with
    /// </summary>
    /// <remarks>
    /// The IAP goes into RAM as it arrives and nothing checks it, so it has to arrive complete and
    /// <c>StartIap</c> has to come after the last of it. A firmware segment is always full, because
    /// the IAP only knows it has a block when the DMA transfer of 2 KB completes, while the length
    /// in the verification request is that of the file, without the padding
    /// </remarks>
    [TestCase("M997")]
    [TestCase("M997 B0")]
    [TestCase("M997 S0")]
    [TestCase("M997 B0 S0")]
    public async Task M997FlashesTheMainBoardThroughTheIap(string code)
    {
        await using JobBench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(code);

        IReadOnlyList<byte[]> segments = bench.CanMaster.FlashedSegments;
        IReadOnlyList<FlashVerify> verifications = bench.CanMaster.FlashVerifications;
        SbcRequest[] requests = [.. bench.CanMaster.Transfers.Where(transfer => transfer.Direction == TransferDirection.FromSbc)
                                 .SelectMany(transfer => transfer.Packets).Select(packet => packet.SbcRequest)
                                 .Where(request => request is SbcRequest.WriteIap or SbcRequest.StartIap)];
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty);

            Assert.That(IapSent(bench), Is.EqualTo(Iap));
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.WriteIap).Select(packet => packet.Data.Length),
                        Is.EqualTo(new[] { IapSegmentSize, Iap.Length - IapSegmentSize }), "no packet is longer than the IAP segment size");
            Assert.That(requests, Is.EqualTo(new[] { SbcRequest.WriteIap, SbcRequest.WriteIap, SbcRequest.StartIap }),
                        "the IAP starts once it is all in RAM");

            Assert.That(segments.All(segment => segment.Length == FirmwareSegmentSize), Is.True, "a segment is always full");
            Assert.That(segments.SelectMany(segment => segment), Is.EqualTo(Padded(Firmware)));

            Assert.That(verifications, Has.Count.EqualTo(1));
            Assert.That(verifications[0].FirmwareLength, Is.EqualTo((uint)Firmware.Length), "the length is the file's, without the padding");
            Assert.That(verifications[0].Crc16, Is.EqualTo(CRC16.Calculate(Firmware)));

            Assert.That(bench.CanMaster.CanMessages<CanMessageUpdateYourFirmware>(), Is.Empty, "the main board is not on the far end of the bus");
            Assert.That(bench.CanMaster.CanMessages<CanMessageReturnInfo>(), Is.Empty);
        });
    }

    /// <summary>
    /// The machine is updating from the start to the end, although the link is down for all of that
    /// time. What follows is the controller restarting, so the program stops: systemd starts it
    /// again and it finds the new firmware
    /// </summary>
    [Test]
    public async Task M997ReportsTheUpdateAndThenStopsTheProgram()
    {
        await using JobBench bench = await StartAsync();
        CancellationToken stopping = bench.Host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration = stopping.Register(stopped.SetResult);

        bench.CanMaster.PauseArming();
        Task<string> code = bench.Host.ExecuteCodeAsync("M997 B0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Updating);
        bench.CanMaster.ResumeArming();

        await code;
        Assert.That(bench.Host.Model.State.Status, Is.EqualTo(MachineStatus.Updating), "still updating once the firmware is written");

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// M997 P names another firmware file. The IAP is still the one the controller reports
    /// </summary>
    [Test]
    public async Task M997PFlashesTheNamedFile()
    {
        byte[] other = Image(100, seed: 50);
        await using JobBench bench = await StartAsync(sd =>
        {
            WriteBinaries(sd);
            sd.WriteFirmware("other.bin", other);
        });

        await bench.Host.ExecuteCodeAsync("M997 B0 P\"other.bin\"");

        Assert.Multiple(() =>
        {
            Assert.That(IapSent(bench), Is.EqualTo(Iap));
            Assert.That(bench.CanMaster.FlashedSegments.SelectMany(segment => segment), Is.EqualTo(Padded(other)));
            Assert.That(bench.CanMaster.FlashVerifications[0].FirmwareLength, Is.EqualTo((uint)other.Length));
        });
    }

    /// <summary>
    /// A binary missing from 0:/firmware is looked for in 0:/sys, where they used to be kept
    /// </summary>
    [Test]
    public async Task M997FallsBackOnTheSystemDirectory()
    {
        await using JobBench bench = await StartAsync(sd =>
        {
            sd.WriteSys(IapFile, "abcd");
            sd.WriteSys(FirmwareFile, "efgh");
        });

        await bench.Host.ExecuteCodeAsync("M997 B0");

        Assert.Multiple(() =>
        {
            Assert.That(IapSent(bench), Is.EqualTo("abcd"u8.ToArray()));
            Assert.That(bench.CanMaster.FlashedSegments.SelectMany(segment => segment).Take(4), Is.EqualTo("efgh"u8.ToArray()));
        });
    }

    /// <summary>
    /// A binary that is not there stops the update before anything happens to the controller, which
    /// is the last moment it can
    /// </summary>
    [TestCase(IapFile, "Failed to find IAP file " + IapFile)]
    [TestCase(FirmwareFile, "Failed to find firmware file " + FirmwareFile)]
    public async Task M997RefusesAMissingBinary(string missing, string error)
    {
        await using JobBench bench = await StartAsync(sd =>
        {
            foreach (string name in new[] { IapFile, FirmwareFile }.Where(name => name != missing))
            {
                sd.WriteFirmware(name, Iap);
            }
        });

        string reply = await bench.Host.ExecuteCodeAsync("M997 B0");

        Assert.That(reply, Does.Contain(error));
        AssertNothingSent(bench);
    }

    /// <summary>
    /// A controller that has not named both files cannot be updated, because the IAP cannot be chosen
    /// </summary>
    [TestCase("", IapFile)]
    [TestCase(FirmwareFile, "")]
    public async Task M997RefusesAnUnnamedBinary(string firmwareFile, string iapFile)
    {
        await using JobBench bench = await StartAsync(firmwareFile: firmwareFile, iapFile: iapFile);

        string reply = await bench.Host.ExecuteCodeAsync("M997 B0");

        Assert.That(reply, Does.Contain("Cannot update firmware because IAP and firmware filenames are unknown"));
        AssertNothingSent(bench);
    }

    /// <summary>
    /// The IAP is what updates the main firmware, so a module list without 0 does not start it
    /// </summary>
    [Test]
    public async Task M997WithoutModuleZeroLeavesTheControllerAlone()
    {
        await using JobBench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M997 B0 S2");

        Assert.That(reply, Does.Contain("S0 required to update DuetCANMaster"));
        AssertNothingSent(bench);
    }

    /// <summary>
    /// The IAP erases what it wrote after a bad checksum, so the only retry is the whole image again
    /// </summary>
    [Test]
    public async Task M997SendsTheImageAgainAfterABadChecksum()
    {
        await using JobBench bench = await StartAsync();
        bench.CanMaster.FailNextFlashVerifications(1);

        string reply = await bench.Host.ExecuteCodeAsync("M997 B0");

        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(bench.CanMaster.FlashVerifications, Has.Count.EqualTo(2));
            Assert.That(bench.CanMaster.FlashedSegments.SelectMany(segment => segment),
                        Is.EqualTo(Padded(Firmware).Concat(Padded(Firmware))));
            Assert.That(bench.CanMaster.SbcPackets(SbcRequest.StartIap), Has.Count.EqualTo(1), "the IAP is still running");
        });
    }

    /// <summary>
    /// Three bad checksums are the end of it: the IAP gives up at the same point and leaves the board
    /// in its ROM bootloader, so the code has to say that the firmware is not on the board. The
    /// program carries on, looking for a controller again in case one is put back
    /// </summary>
    [Test]
    public async Task M997GivesUpAfterThreeBadChecksums()
    {
        await using JobBench bench = await StartAsync();
        bench.CanMaster.FlashVerdict = 0;

        string reply = await CodeReplyOrErrorAsync(bench, "M997 B0");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain(GaveUp));
            Assert.That(bench.CanMaster.FlashVerifications, Has.Count.EqualTo(3));
        });

        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>A code's reply, or the message of the exception it finished with</summary>
    private static async Task<string> CodeReplyOrErrorAsync(JobBench bench, string code)
    {
        try
        {
            return await bench.Host.ExecuteCodeAsync(code);
        }
        catch (Exception e) when (e is not TimeoutException)
        {
            return e.Message;
        }
    }
}
