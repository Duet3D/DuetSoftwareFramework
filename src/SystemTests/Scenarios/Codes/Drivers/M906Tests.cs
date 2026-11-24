using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using System.Collections.Generic;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.Shared;

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

    /// <summary>
    /// An axis whose drivers are on two boards gets one request per board, and every extruder named
    /// gets its own
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m906-set-motor-current.yaml</c>, which maps Y onto a driver of the board
    /// at address 3 and one at address 1. RepRapFirmware's SetRemoteDriverValues sends one message per
    /// board. The order the requests go out in does not matter
    /// </remarks>
    [Test]
    public async Task M906SendsOneRequestPerBoardOfAnAxis()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config,
            $"M584 E{DriversRig.MainDriver(3)}:{DriversRig.MainDriver(4)}:{DriversRig.MainDriver(5)}",
            $"M584 Y{DriversRig.ClosedLoopDriver(0)}:{DriversRig.MainDriver(1)}");

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 X500 Y500 Z500 E500")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(sent.Select(m => (m.Board, (int)m.Message.DriversToUpdate)),
                    Is.EquivalentTo(new[]
                    {
                        (DriversRig.MainDriverBoard, 1 << 0),
                        (DriversRig.MainDriverBoard, 1 << 1),
                        (DriversRig.ClosedLoopBoard, 1 << 0),
                        (DriversRig.MainDriverBoard, 1 << 2),
                        (DriversRig.MainDriverBoard, 1 << 3),
                        (DriversRig.MainDriverBoard, 1 << 4),
                        (DriversRig.MainDriverBoard, 1 << 5)
                    }),
                    "X and Z, Y's driver on each of its boards, and one request per extruder");
        Assert.That(sent.Select(m => m.Message.Values[0]), Is.All.EqualTo(500.0f));

        Assert.That((await bench.Host.ExecuteCodeAsync("M906")).TrimEnd(),
                    Is.EqualTo("Motor current (mA) - X:500, Y:500, Z:500, E:500:500:500, idle factor 30%, timeout 30.0 sec"));
    }

    /// <summary>
    /// A board refusing a current makes M906 an error carrying the board's reason, the other drives
    /// are still told, and the object model keeps the current that was asked for
    /// </summary>
    /// <remarks>
    /// Duet3Expansion's Move::SetMotorCurrent limits the current to what the driver can deliver and
    /// answers "Driver 1.0 limited to ...mA" as an error. RRF's Move::SetMotorCurrent has already
    /// stored the requested current by then, and GCodes2.cpp case 906 carries on to the next drive,
    /// keeping the worst result
    /// </remarks>
    [Test]
    public async Task M906ReportsABoardThatLimitsTheCurrent()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        string refusal = $"Driver {DriversRig.MainDriver(0)} limited to 6300mA";
        bench.CanMaster.OnCanMessage((ushort)CanMessageMultipleDrivesRequestMotorCurrents.MessageType,
                                     (fake, header, _) => fake.InjectStandardReply(header,
                                         header.DstAddress == DriversRig.MainDriverBoard ? CodeResult.Error : CodeResult.Ok,
                                         header.DstAddress == DriversRig.MainDriverBoard ? refusal : string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync("M906 X7000 E900");

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(reply, Does.Contain(refusal), "the board's reason is carried back");
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>().Select(m => m.Board),
                        Is.EquivalentTo(new[] { DriversRig.MainDriverBoard, DriversRig.ClosedLoopBoard }),
                        "the refusal for X does not stop the extruder being told");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(7000),
                        "move.axes[0].current holds what was asked for, as RRF's motorCurrents does");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(900));
        });
    }

    /// <summary>
    /// A drive's current is refused when negative, and an idle factor outside 0 to 100; either
    /// leaves the model as it was and tells no board
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 906 reads an axis with GetNonNegativeFValue and I with
    /// GetLimitedFValue('I', 0.0, 100.0). It reads E with GetFloatArray and does not check it, so it
    /// would store and send a negative extruder current; refusing it as the axes do is the minimum
    /// limit rrf-differences.md section 6.1 records
    /// </remarks>
    [TestCase("M906 X-100")]
    [TestCase("M906 E-100")]
    [TestCase("M906 I101")]
    [TestCase("M906 I-1")]
    public async Task M906RefusesAnOutOfRangeValue(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(400));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(800));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Factor), Is.EqualTo(0.3f).Within(1e-4));
        });
    }

    /// <summary>
    /// A code refused part way keeps what it applied before the refusal, and the boards were told
    /// about it, so the object model and the drivers agree
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 906 applies each drive as it reads it (Move::SetMotorCurrent sends the
    /// request there and then), so a bad value later in the line is refused after the earlier drives
    /// have their new current. The parameters are read in the order axes, E, I, T
    /// </remarks>
    [TestCase("M906 X600 Y-5", 600, 800, 0.3f, TestName = "M906 X600 Y-5 keeps X")]
    [TestCase("M906 X600 E700:700", 600, 800, 0.3f, TestName = "M906 X600 E700:700 keeps X")]
    [TestCase("M906 X600 E700 I40 T0", 600, 700, 0.4f, TestName = "M906 X600 E700 I40 T0 keeps X, E and I")]
    [Category("KnownGap")]
    public async Task M906RefusedPartWayKeepsAndSendsWhatItApplied(string code, int x, int e, float idleFactor)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(x));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Current), Is.EqualTo(400),
                        "Y was never applied");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Current), Is.EqualTo(e));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Factor), Is.EqualTo(idleFactor).Within(1e-4));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout), Is.EqualTo(30.0f).Within(1e-3));

            var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
            Assert.That(sent.Select(m => (m.Board, m.Message.Values[0])),
                        Is.EqualTo(e == 800
                            ? new[] { (DriversRig.MainDriverBoard, (float)x) }
                            : new[] { (DriversRig.MainDriverBoard, (float)x), (DriversRig.ClosedLoopBoard, (float)e) }),
                        "every drive the model holds a new current for was told it");
        });
    }

    /// <summary>
    /// A bare M906 on a machine with no extruders still names the extruders, with nothing after them
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 906 always appends "E", then one ":value" per extruder</remarks>
    [Test]
    public async Task M906ReportsAMachineWithNoExtruders()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M584 E");

        Assert.That((await bench.Host.ExecuteCodeAsync("M906")).TrimEnd(),
                    Is.EqualTo("Motor current (mA) - X:400, Y:400, Z:400, E, idle factor 30%, timeout 30.0 sec"));
    }

    /// <summary>
    /// Changing the idle factor reaches the drivers that are idling already, at the new percentage
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::SetIdleCurrentFactor walks the drives in the idle state and sends them
    /// CanInterface::SetRemoteDriversIdle with the new factor. The drives go idle when the idle
    /// time-out runs out after a move (Move.cpp Move::Spin, SetDriversIdle)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M906IdleFactorReachesDriversThatAreIdle()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M906 T0.1");

        await bench.Host.ExecuteCodeAsync("G1 X10 F6000");
        await bench.Host.ExecuteCodeAsync("M400");
        await bench.CanMaster.WaitForCanMessageAsync<CanMessageMultipleDrivesRequestDriverStateControl>(
            message => message.Values[0].Mode == DriverStateControl.DriverIdle, timeoutMs: 5_000);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 I50")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestDriverStateControl>();
        Assert.That(sent, Is.Not.Empty, "the idle drivers are told the new factor");
        Assert.Multiple(() =>
        {
            Assert.That(sent.Select(m => m.Message.Values[0].Mode), Is.All.EqualTo(DriverStateControl.DriverIdle));
            Assert.That(sent.Select(m => m.Message.Values[0].IdlePercent), Is.All.EqualTo(50));
            Assert.That(sent.Any(m => m.Board == DriversRig.MainDriverBoard && (m.Message.DriversToUpdate & 1) != 0),
                        "X's driver, which the move left idle, is among them");
        });
    }

    /// <summary>
    /// A fractional current reaches the driver as it was given, while the report and the object
    /// model round it
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp keeps motorCurrents as floats and sends the float; GetMotorCurrent rounds it
    /// with lrintf for the report and the object model
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M906SendsAFractionalCurrentAsGiven()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 X600.4")).Trim(), Is.Empty);

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>().Message.Values[0],
                        Is.EqualTo(600.4f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(600));
        });
    }
}
