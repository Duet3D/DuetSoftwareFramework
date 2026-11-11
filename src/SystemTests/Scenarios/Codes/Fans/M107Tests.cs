using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Fans.FanSetup;

namespace SystemTests.Scenarios.Codes.Fans;

/// <summary>M107, turning a fan off</summary>
[TestFixture]
[GCode("M107")]
public class M107Tests : BenchFixture
{
    /// <summary>A bare M107 with no tool selected turns off fan 0 and only fan 0</summary>
    /// <remarks>
    /// GCodes2.cpp case 107 calls SetMappedFanSpeed(gb, 0.0), and SetMappedFanSpeed (GCodes.cpp)
    /// addresses fan 0 when no tool is selected
    /// </remarks>
    [Test]
    public async Task M107TurnsOffMappedFanWithoutTool()
    {
        await using Bench bench = await Bench.StartAsync(TwoFanConfig);

        await bench.Host.ExecuteCodeAsync("M106 P0 S255");
        await bench.Host.ExecuteCodeAsync("M106 P1 S0.75");
        await bench.Host.ExecuteCodeAsync("M107");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.Zero,
                        "bare M107 with no tool turns off fan 0: fans[0].requestedValue 0 (GCodes.cpp SetMappedFanSpeed)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[1]!.RequestedValue), Is.EqualTo(0.75).Within(1e-3),
                        "bare M107 leaves fans[1].requestedValue alone (GCodes2.cpp case 107)");
        });

        (byte board, CanMessageSetFanSpeed sent) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sent.FanNumber, Is.EqualTo(0),
                        "the last speed on the bus is fan 0's: M107 addressed the mapped fan, which with no "
                        + "tool selected is fan 0 (GCodes.cpp SetMappedFanSpeed)");
            Assert.That(sent.Pwm, Is.Zero, "and asked for nothing");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>().Count(m => m.Message.FanNumber == 1),
                        Is.EqualTo(1), "fan 1 heard only its own M106: M107 sent it nothing");
        });
    }

    /// <summary>M107 ignores its P parameter and still addresses the mapped fan</summary>
    /// <remarks>
    /// GCodes2.cpp case 107 reads no parameter at all: it is one call to
    /// SetMappedFanSpeed(gb, 0.0), so M107 P1 turns off fan 0 (no tool selected) and fan 1 keeps
    /// its speed
    /// </remarks>
    [Test]
    public async Task M107IgnoresPParameter()
    {
        await using Bench bench = await Bench.StartAsync(TwoFanConfig);

        await bench.Host.ExecuteCodeAsync("M106 P0 S255");
        await bench.Host.ExecuteCodeAsync("M106 P1 S0.75");
        await bench.Host.ExecuteCodeAsync("M107 P1");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.Zero,
                        "M107 P1 still turns off the mapped fan 0: fans[0].requestedValue 0 (GCodes2.cpp case 107 reads no P)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[1]!.RequestedValue), Is.EqualTo(0.75).Within(1e-3),
                        "M107 P1 leaves fans[1].requestedValue alone (GCodes2.cpp case 107 reads no P)");
        });

        (_, CanMessageSetFanSpeed sent) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.FanNumber, Is.EqualTo(0),
                        "the P1 went nowhere: the message names fan 0, the mapped one");
            Assert.That(sent.Pwm, Is.Zero);
        });
    }
}
