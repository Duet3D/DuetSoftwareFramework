using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M917, the standstill current percentages</summary>
[TestFixture]
[GCode("M917")]
public class M917Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

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
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M917 E50");

        (byte board, CanMessageMultipleDrivesRequestStandstillCurrentFactor sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStandstillCurrentFactor>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent.Values[0], Is.EqualTo(50.0f).Within(1e-3));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M917")).TrimEnd(),
                    Is.EqualTo("Motor standstill current % of normal - X:71, Y:71, Z:71, E:50"),
                    "and the report carries every drive (m917-standstill-current-for-extruder.yaml)");
    }
}
