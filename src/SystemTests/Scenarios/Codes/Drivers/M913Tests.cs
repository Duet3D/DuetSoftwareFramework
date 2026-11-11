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
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M913 X80 Y80 Z80 E80");

        Assert.That((await bench.Host.ExecuteCodeAsync("M913")).TrimEnd(),
                    Is.EqualTo("Motor current % of normal - X:80, Y:80, Z:80, E:80"));
    }
}
