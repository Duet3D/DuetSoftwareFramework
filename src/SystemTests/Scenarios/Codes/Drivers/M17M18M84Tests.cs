using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using System.Globalization;
using System.Threading;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M17, M18 and M84, enabling and disabling the drivers, and the idle timeout</summary>
[TestFixture]
[GCode("M17", "M18", "M84")]
public class M17M18M84Tests : BenchFixture
{
    /// <summary>
    /// M17 and M18 send a bare driver state: the idle percentage belongs to the idle state alone
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-multiple-drivers-per-axis.yaml</c> captures <c>M18 Z</c> as a
    /// setDriverStates carrying the two zero bytes of a disabled driver. RepRapFirmware only puts an
    /// idle percentage in the message from SetRemoteDriversIdle; EnableRemoteDrivers and
    /// DisableRemoteDrivers send the state on its own (CanInterface.cpp:988-1017)
    /// </remarks>
    [Test]
    public async Task M18SendsABareDriverState()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M18 Z");

        var payloads = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestDriverStateControl>();
        Assert.That(payloads, Has.Count.EqualTo(1), "one board carries Z, so one request goes out");
        Assert.Multiple(() =>
        {
            Assert.That(payloads[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestDriverStateControl { DriversToUpdate = 0b1 }.GetActualDataLength()),
                        "carrying one driver's value and stopping there");
            Assert.That(payloads[0].Payload[^2..], Is.EqualTo(new byte[] { 0, 0 }),
                        "which is the disabled state with no idle percentage beside it");
        });

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Homed), Is.False,
                    "and a de-energised axis is no longer known to be where it says it is");
    }

    /// <summary>
    /// M18 with an axis letter de-energises that axis' drivers and marks the axis not homed; a bare
    /// M84 does the same for every axis; M17 re-energises without touching the homed flags
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp cases 17/18/84: a named axis gets SetAxisNotHomed + DisableDrivers, a bare
    /// M18/M84 calls GCodes::DisableDrives (GCodes.cpp), which is DisableAllDrivers +
    /// SetAllAxesNotHomed. M17 only calls EnableDrivers
    /// </remarks>
    [Test]
    public async Task M17M18M84DriveEnableAndHomedFlags()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, Machines.Xye.Homed);

        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        Assert.That(axis0.Homed, Is.True,
                    "the bench config's G92 marks X homed");

        await bench.Host.ExecuteCodeAsync("M18 X");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.Homed, Is.False,
                        "M18 X clears move.axes[0].homed (RRF GCodes2.cpp case 18, SetAxisNotHomed)");
            Assert.That(axis1.Homed, Is.True,
                        "M18 X leaves move.axes[1].homed alone, Y was not named");
        });

        await bench.Host.ExecuteCodeAsync("G92 X0");
        Assert.That(axis0.Homed, Is.True,
                    "G92 X marks X homed again");

        await bench.Host.ExecuteCodeAsync("M84");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.Homed, Is.False,
                        "bare M84 clears move.axes[0].homed (RRF GCodes.cpp DisableDrives, SetAllAxesNotHomed)");
            Assert.That(axis1.Homed, Is.False,
                        "bare M84 clears move.axes[1].homed (RRF GCodes.cpp DisableDrives, SetAllAxesNotHomed)");
        });

        string reply = await bench.Host.ExecuteCodeAsync("M17");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "M17 succeeds silently (RRF GCodes2.cpp case 17)");
            Assert.That(axis0.Homed, Is.False,
                        "M17 does not mark an axis homed (RRF GCodes2.cpp case 17 only enables drivers)");
        });
    }

    /// <summary>
    /// M84 S sets the idle timeout without de-energising anything: S makes the code 'seen', so the
    /// disable-everything branch never runs and the homed flags stay
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 84: gb.Seen('S') sets seen and calls Move::SetIdleTimeout; DisableDrives
    /// only runs when nothing at all was seen. move.idle.timeout is reported in seconds
    /// (Move.cpp idle table, 0.001f * idleTimeout)
    /// </remarks>
    [Test]
    public async Task M84SSetsIdleTimeoutWithoutDisabling()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, Machines.Xye.Homed);

        await bench.Host.ExecuteCodeAsync("M84 S45");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout), Is.EqualTo(45.0).Within(1e-3),
                        "M84 S sets move.idle.timeout in seconds (RRF GCodes2.cpp case 84, SetIdleTimeout)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed), Is.True,
                        "M84 S does not disable the motors, so X stays homed (RRF GCodes2.cpp case 84, seen branch)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Homed), Is.True,
                        "M84 S does not disable the motors, so Y stays homed (RRF GCodes2.cpp case 84, seen branch)");
        });
    }
}
