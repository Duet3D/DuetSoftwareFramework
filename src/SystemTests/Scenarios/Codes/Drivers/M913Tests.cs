using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M913, the motor current percentages</summary>
[TestFixture]
[GCode("M913")]
public class M913Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

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
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M906 E400");

        await bench.Host.ExecuteCodeAsync("M913 E60");

        (byte board, CanMessageMultipleDrivesRequestMotorCurrents sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(BitConverter.GetBytes(sent.Values[0]), Is.EqualTo(BitConverter.GetBytes(400.0f * (0.01f * 60))),
                        "400mA at 60% is 239.99998 in single precision, which is the value the "
                        + "reference recorded from the firmware");
        });

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PercentCurrent), Is.EqualTo(60),
                    "move.extruders[0].percentCurrent records the percentage (RRF Move.cpp extruder "
                    + "table, GetMotorCurrent(ExtruderToLogicalDrive(...), 913))");
    }

    /// <summary>A bare M913 reports the percentage of each drive</summary>
    /// <remarks><c>testcases/drivers/m913-report-current-percentage.yaml</c></remarks>
    [Test]
    public async Task M913ReportsTheCurrentPercentages()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M913 X80 Y80 Z80 E80");

        Assert.That((await bench.Host.ExecuteCodeAsync("M913")).TrimEnd(),
                    Is.EqualTo("Motor current % of normal - X:80, Y:80, Z:80, E:80"));
    }

    /// <summary>
    /// A percentage above 100 runs the drive at its full current, and the object model reports 100
    /// </summary>
    /// <remarks>RRF Move2.cpp Move::SetMotorCurrent case 913 constrains the fraction to 0..1</remarks>
    [Test]
    public async Task M913ClampsAPercentageAbove100()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M913 X150")).Trim(), Is.Empty);

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>().Message.Values[0],
                        Is.EqualTo(400.0f), "the configured current and no more");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentCurrent), Is.EqualTo(100));
        });
    }

    /// <summary>
    /// Zero is a valid percentage, which a power fail script uses to take the current off a drive
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 906/913 reads the axes with GetNonNegativeFValue: "we must allow zero in
    /// M913 power fail scripts"
    /// </remarks>
    [Test]
    public async Task M913ZeroTakesTheCurrentOff()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M913 X0")).Trim(), Is.Empty);

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>().Message.Values[0],
                        Is.EqualTo(0.0f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentCurrent), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(400),
                        "the configured current is kept for when the percentage is restored");
        });
    }

    /// <summary>
    /// A negative percentage is refused, leaving the model as it was and telling no board
    /// </summary>
    /// <remarks>
    /// RRF reads an axis with GetNonNegativeFValue, which refuses it, but reads E with GetFloatArray
    /// and clamps it to 0 in Move::SetMotorCurrent. Refusing it for the extruders too is the minimum
    /// limit rrf-differences.md section 3.1 records
    /// </remarks>
    [TestCase("M913 X-10")]
    [TestCase("M913 E-10")]
    public async Task M913RefusesANegativePercentage(string code)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync(code);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>(), Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentCurrent), Is.EqualTo(100));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PercentCurrent), Is.EqualTo(100));
        });
    }

    /// <summary>
    /// A current set with M906 while a percentage is in force goes to the driver scaled by it, and
    /// restoring 100% sends the full current
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::SetMotorCurrent sends motorCurrents * motorCurrentFraction for both 906
    /// and 913, which is how homing files reduce the current and put it back
    /// </remarks>
    [Test]
    public async Task M913ScalesACurrentSetWhileItIsInForce()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M913 X50");

        Assert.That((await bench.Host.ExecuteCodeAsync("M906 X800")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>().Message.Values[0],
                        Is.EqualTo(800.0f * (0.01f * 50)), "half of the new current");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Current), Is.EqualTo(800));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentCurrent), Is.EqualTo(50));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M913 X100")).Trim(), Is.Empty);
        Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>().Message.Values[0],
                    Is.EqualTo(800.0f), "and 100% is the whole of it again");
    }

    /// <summary>
    /// A bare M913 on a machine with no extruders still names the extruders, with nothing after them
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m913-motor-current-percentage.yaml</c> records
    /// "Motor current % of normal - X:70, Y:70, Z:70, E" from a bench with no extruders
    /// </remarks>
    [Test]
    public async Task M913ReportsAMachineWithNoExtruders()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M584 E", "M913 X70 Y70 Z70");

        Assert.That((await bench.Host.ExecuteCodeAsync("M913")).TrimEnd(),
                    Is.EqualTo("Motor current % of normal - X:70, Y:70, Z:70, E"));
    }

    /// <summary>
    /// A fractional percentage scales the current as it was given, while the report and the object
    /// model round it
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp keeps motorCurrentFraction as a float (0.01 * 50.5) and sends the current
    /// scaled by it; GetMotorCurrent rounds the percentage with lrintf for the object model
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M913ScalesByAFractionalPercentageAsGiven()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M913 X50.5")).Trim(), Is.Empty);

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestMotorCurrents>().Message.Values[0],
                        Is.EqualTo(400.0f * (0.01f * 50.5f)));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PercentCurrent), Is.EqualTo(50));
        });
    }
}
