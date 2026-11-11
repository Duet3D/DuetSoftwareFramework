using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using System.Collections.Generic;
using DuetAPI.Utility;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M906, motor currents, the idle current factor and the idle timeout</summary>
[TestFixture]
[GCode("M906")]
public class M906Tests : BenchFixture
{
    /// <summary>
    /// M906 sets the peak motor current of each axis it names, one message per axis, and records it
    /// in move.axes[].current
    /// </summary>
    /// <remarks><c>testcases/drivers/m906-set-motor-current.yaml</c></remarks>
    [Test]
    public async Task M906SetsMotorCurrentPerAxis()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 X600 Y600 Z600")).Trim(), Is.Empty,
                    "the boards took the currents");

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(sent, Has.Count.EqualTo(3),
                    "three axes are three requests, one per drive (RRF Move2.cpp SetMotorCurrent)");
        Assert.Multiple(() =>
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.That(sent[axis].Board, Is.EqualTo(DriversRig.MainDriverBoard));
                Assert.That(sent[axis].Message.DriversToUpdate, Is.EqualTo(1 << axis),
                            "each request names only its own drive's driver");
                Assert.That(sent[axis].Message.Values[0], Is.EqualTo(600.0f).Within(1e-3));
            }
        });

        Assert.That(bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestMotorCurrents>()
                                   .Select(payload => payload.Payload.Length),
                    Is.All.EqualTo(new CanMessageMultipleDrivesRequestMotorCurrents { DriversToUpdate = 0b1 }.GetActualDataLength()),
                    "and stops after the one value it carries, rather than sending the whole struct. "
                    + "The length is the message's own, worked out from its bitmap");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(600));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Current), Is.EqualTo(600));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Current), Is.EqualTo(600));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(800),
                        "and the extruder, which the code did not name, keeps the current it had");
        });
    }

    /// <summary>
    /// M906 E reaches the extruder's own board, and I and T set the idle factor and time-out
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m906-idle-timeout-and-extruder-current.yaml</c> and
    /// <c>m906-idle-current-percentage.yaml</c>. The extruder is on the second board, so this is also
    /// what proves a current reaches one; I and T are whole-machine settings and go nowhere
    /// </remarks>
    [Test]
    public async Task M906ExtruderCurrentIdleFactorAndTimeout()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 E650 I40 T20")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(sent, Has.Count.EqualTo(1), "only the extruder was named, so only it is sent");
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.ClosedLoopBoard),
                        "the extruder's current goes to the board its driver is on");
            Assert.That(sent[0].Message.DriversToUpdate, Is.EqualTo(1), "driver 0 of that board");
            Assert.That(sent[0].Message.Values[0], Is.EqualTo(650.0f).Within(1e-3));
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(650),
                        "move.extruders[0].current records it");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Factor), Is.EqualTo(0.4f).Within(1e-4),
                        "M906 I sets move.idle.factor as a fraction (RRF Move.cpp idle table)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout), Is.EqualTo(20.0f).Within(1e-3),
                        "and T the time-out in seconds, which RRF 3.6 moved onto M906");
        });
    }

    /// <summary>
    /// A bare M906 reports the configured currents, the idle factor as a percentage and the time-out
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m906-report-motor-current.yaml</c>, whose reply is
    /// "Motor current (mA) - X:400, Y:400, Z:400, E:800, idle factor 30%, timeout 30.0 sec" against
    /// the same configuration this bench boots from
    /// </remarks>
    [Test]
    public async Task M906ReportsCurrentsIdleFactorAndTimeout()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M906");
        Assert.That(reply.TrimEnd(),
                    Is.EqualTo("Motor current (mA) - X:400, Y:400, Z:400, E:800, idle factor 30%, timeout 30.0 sec"),
                    "the wording and ordering configuration tools parse (RRF GCodes2.cpp case 906)");
    }

    /// <summary>
    /// M906 refuses an idle time-out of zero the same way
    /// </summary>
    /// <remarks><c>testcases/drivers/error-missing-parameters-drivers.yaml</c></remarks>
    [Test]
    public async Task M906RefusesAZeroIdleTimeout()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        float before = await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout);
        string reply = await bench.Host.ExecuteCodeAsync("M906 T0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout), Is.EqualTo(before),
                        "and move.idle.timeout keeps the value it had");
        });
    }
}
