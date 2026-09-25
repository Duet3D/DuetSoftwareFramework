using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M906, M913 and M917 against the rig the regression suite records on: the currents each puts on the
/// bus, the values they leave in the object model, and the reports a bare form answers with
/// </summary>
/// <remarks>
/// The bench half of <c>lib/DuetRegressionTesting/testcases/drivers/m906-*.yaml</c>,
/// <c>m913-*.yaml</c> and <c>m917-*.yaml</c>. What separates these three from M92 and M350 is that a
/// current is set one drive at a time - RepRapFirmware's Move::SetMotorCurrent is called once per
/// axis or extruder and sends its own request each time (Move2.cpp) - so naming three axes puts three
/// messages on the bus rather than one carrying three values
/// </remarks>
[TestFixture]
public class DriverCurrentTests : BenchFixture
{

    /// <summary>
    /// M906 sets the peak motor current of each axis it names, one message per axis, and records it
    /// in move.axes[].current
    /// </summary>
    /// <remarks><c>testcases/drivers/m906-set-motor-current.yaml</c></remarks>
    [Test]
    public async Task M906SetsMotorCurrentPerAxis()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M906 X600 Y600 Z600")).Trim(), Is.Empty,
                    "the boards took the currents");

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(sent, Has.Count.EqualTo(3),
                    "three axes are three requests, one per drive (RRF Move2.cpp SetMotorCurrent)");
        Assert.Multiple(() =>
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.That(sent[axis].Board, Is.EqualTo(DriversBench.MainDriverBoard));
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
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M906 E650 I40 T20")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(sent, Has.Count.EqualTo(1), "only the extruder was named, so only it is sent");
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversBench.ClosedLoopBoard),
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
        await using JobBench bench = await DriversBench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M906");
        Assert.That(reply.TrimEnd(),
                    Is.EqualTo("Motor current (mA) - X:400, Y:400, Z:400, E:800, idle factor 30%, timeout 30.0 sec"),
                    "the wording and ordering configuration tools parse (RRF GCodes2.cpp case 906)");
    }

    /// <summary>
    /// M913 scales the configured current, and what goes to the driver is the resulting current in mA
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m913-motor-current-percentage.yaml</c>. M913 is not a setting of its own
    /// on the driver, so the percentage is applied here and the product sent; the arithmetic is
    /// RepRapFirmware's, in single precision, so 400mA at 70% is what its reference holds to the bit
    /// </remarks>
    [Test]
    public async Task M913ScalesTheConfiguredCurrent()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M913 X70 Y70 Z70")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(sent, Has.Count.EqualTo(3), "one request per axis named");
        Assert.That(sent[0].Message.Values[0], Is.EqualTo(400.0f * (0.01f * 70)),
                    "the percentage becomes a fraction first and scales the current, as "
                    + "Move::SetMotorCurrent does (Move2.cpp case 913)");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentCurrent), Is.EqualTo(70),
                        "move.axes[].percentCurrent records the percentage itself");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(400),
                        "and move.axes[].current keeps the current M906 configured");
        });
    }

    /// <summary>
    /// M913 on the extruder reaches the second board, with the same scaling
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m913-current-percentage-for-extruder.yaml</c>, whose CAN value is
    /// 239.99998 - 400mA at 60% worked out in single precision
    /// </remarks>
    [Test]
    public async Task M913ScalesTheExtruderCurrent()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: "M906 E400");

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M913 E60");

        (byte board, CanMessageMultipleDrivesRequestMotorCurrents sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversBench.ClosedLoopBoard));
            Assert.That(BitConverter.GetBytes(sent.Values[0]), Is.EqualTo(BitConverter.GetBytes(400.0f * (0.01f * 60))),
                        "400mA at 60% is 239.99998 in single precision, which is the value the "
                        + "reference recorded from the firmware");
        });

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PercentCurrent), Is.EqualTo(60),
                    "move.extruders[0].percentCurrent records the percentage. RepRapFirmware's object "
                    + "model reports axis 0's instead, because it passes the extruder number straight "
                    + "to Move::GetMotorCurrent without ExtruderToLogicalDrive (Move.cpp:297) - which "
                    + "m913-current-percentage-for-extruder.yaml records as a known bug rather than a "
                    + "behaviour to reproduce");
    }

    /// <summary>A bare M913 reports the percentage of each drive</summary>
    /// <remarks><c>testcases/drivers/m913-report-current-percentage.yaml</c></remarks>
    [Test]
    public async Task M913ReportsTheCurrentPercentages()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: "M913 X80 Y80 Z80 E80");

        Assert.That((await bench.Host.ExecuteCodeAsync("M913")).TrimEnd(),
                    Is.EqualTo("Motor current % of normal - X:80, Y:80, Z:80, E:80"));
    }

    /// <summary>
    /// M917 sets the standstill current percentage, which is its own setting on the driver rather
    /// than a scaling applied here
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m917-standstill-current-reduction.yaml</c> and
    /// <c>m917-standstill-current-for-extruder.yaml</c>: the percentage itself goes out, in its own
    /// message type, one per drive
    /// </remarks>
    [Test]
    public async Task M917SetsTheStandstillPercentage()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M917 X60 Y60 Z60")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStandstillCurrentFactor>();
        Assert.That(sent, Has.Count.EqualTo(3), "one request per axis named");
        Assert.Multiple(() =>
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.That(sent[axis].Message.DriversToUpdate, Is.EqualTo(1 << axis));
                Assert.That(sent[axis].Message.Values[0], Is.EqualTo(60.0f).Within(1e-3),
                            "the percentage itself, not a current");
            }
        });

        Assert.That(bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStandstillCurrentFactor>()
                                   .Select(payload => payload.Payload.Length),
                    Is.All.EqualTo(new CanMessageMultipleDrivesRequestStandstillCurrentFactor { DriversToUpdate = 0b1 }.GetActualDataLength()));

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentStstCurrent), Is.EqualTo(60),
                    "move.axes[].percentStstCurrent records it");
    }

    /// <summary>M917 on the extruder reaches the board its driver is on</summary>
    /// <remarks><c>testcases/drivers/m917-standstill-current-for-extruder.yaml</c></remarks>
    [Test]
    public async Task M917SetsTheExtruderStandstillPercentage()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M917 E50");

        (byte board, CanMessageMultipleDrivesRequestStandstillCurrentFactor sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStandstillCurrentFactor>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversBench.ClosedLoopBoard));
            Assert.That(sent.Values[0], Is.EqualTo(50.0f).Within(1e-3));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M917")).TrimEnd(),
                    Is.EqualTo("Motor standstill current % of normal - X:71, Y:71, Z:71, E:50"),
                    "and the report carries every drive (m917-standstill-current-for-extruder.yaml)");
    }

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
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
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
}
