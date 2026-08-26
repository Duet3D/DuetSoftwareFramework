using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// Motion through the real engine: a configured axis, a commanded move, and the ScheduleMove
/// packets the link carries for it, captured and decoded on the fake controller
/// </summary>
[TestFixture]
public class MotionTests : SystemTests.Host.BenchFixture
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock");

    /// <summary>
    /// One X axis on driver 1.0 (board 0 runs DuetCANMaster and has no drivers) at 80 steps/mm,
    /// free to move without homing. M953 comes first: with the bus disabled the configuration's
    /// CAN messages would be answered with BusError, as the real controller answers them
    /// </summary>
    private const string OneAxisConfig = """
        M953
        M569 P1.0 S1
        M584 X1.0
        M92 X80
        M906 X800
        M201 X500
        M203 X6000
        M566 X900
        M208 X0:200
        M564 H0 S0
        """;

    [Test]
    public async Task CommandedMoveArrivesAsScheduleMove()
    {
        using ScriptedCanMaster fake = new(SocketPath());
        fake.AckCanRequestsWithStandardReplies();
        await using DcsTestHost host = await DcsTestHost.StartAsync(fake,
            sd => sd.WriteSys("config.g", OneAxisConfig + DcsTestHost.ConfigDoneMarker));
        await host.WaitForConfigDoneAsync();

        await host.ExecuteCodeAsync("G91");
        string reply = await host.ExecuteCodeAsync("G1 X10 F6000");
        Assert.That(reply.Trim(), Is.Empty, "the move was accepted");

        CapturedPacket packet = await fake.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        (ScheduleMoveHeader header, ScheduleMoveDriver[] drivers) = packet.DecodeScheduleMove();

        Assert.Multiple(() =>
        {
            Assert.That(header.TotalDistance, Is.EqualTo(10f).Within(1e-3f), "10 mm commanded");
            Assert.That(drivers, Has.Length.EqualTo(1), "one driver moves");
            Assert.That(drivers[0].BoardAddress, Is.EqualTo(1));
            Assert.That(drivers[0].DriverNumber, Is.Zero);
            Assert.That(drivers[0].IsExtruder, Is.Zero);
            Assert.That(drivers[0].Steps, Is.EqualTo(800), "10 mm at 80 steps/mm");
        });
    }
}
