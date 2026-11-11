using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M350, microstepping and interpolation</summary>
[TestFixture]
[GCode("M350")]
public class M350Tests : BenchFixture
{
    /// <summary>
    /// M350 sets the microstepping, carries the interpolation flag in the message, and rescales the
    /// steps per millimetre so the axis stays calibrated
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m350-microstepping-interpolated.yaml</c>,
    /// <c>m350-microstepping-no-interpolation.yaml</c> and <c>m350-microstepping-extremes.yaml</c>.
    /// Bit 15 of the microstepping field is the interpolation flag, which is what distinguishes the I
    /// parameter reaching the driver from the firmware accepting it and dropping it
    /// </remarks>
    [TestCase(1, false)]
    [TestCase(16, true)]
    [TestCase(256, true)]
    public async Task M350SetsMicrosteppingAndInterpolation(int microstepping, bool interpolated)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync(
                        $"M350 X{microstepping} Y{microstepping} Z{microstepping} I{(interpolated ? 1 : 0)}")).Trim(),
                    Is.Empty);

        (_, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.Values[0].GetMicrostepping() & 0x03FF, Is.EqualTo(microstepping),
                        "the microstepping is in bits 0-9");
            Assert.That((sent.Values[0].GetMicrostepping() & 0x8000) != 0, Is.EqualTo(interpolated),
                        "and bit 15 asks the driver to interpolate");
            Assert.That(sent.Values[0].GetStepsPerUnit(), Is.EqualTo(80.0f * microstepping / 16).Within(1e-3),
                        "with the steps per millimetre rescaled from the microstepping in force "
                        + "(RRF GCodes.cpp ChangeMicrostepping)");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Value),
                        Is.EqualTo(microstepping));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Interpolated),
                        Is.EqualTo(interpolated));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed), Is.False,
                        "and a drive whose microstepping changed is no longer known to be where it "
                        + "says it is (RRF GCodes2.cpp case 350, SetAxisNotHomed)");
        });
    }

    /// <summary>
    /// M350 E reaches the extruder's own board, which is a different board from the axes'
    /// </summary>
    /// <remarks><c>testcases/drivers/m350-microstepping-for-extruder.yaml</c></remarks>
    [Test]
    public async Task M350SetsExtruderMicrostepping()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M350 E64 I1");

        var sent = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent, Has.Count.EqualTo(1), "only the extruder was named");
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b1 }.GetActualDataLength()));
        });

        await bench.Host.ExecuteCodeAsync("M350 E16 I0");
        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(),
                    Is.EqualTo("Microstepping - X:16(on), Y:16(on), Z:16(on), E:16"),
                    "the report suffixes (on) for an interpolating drive and nothing for one that is "
                    + "not (m350-microstepping-for-extruder.yaml)");
    }

    /// <summary>
    /// A bare M350 reports every drive, and a machine that never mentioned M350 reports x16
    /// interpolated, which is the state RepRapFirmware starts every drive in
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m350-report-microstepping.yaml</c>. Move::Init runs
    /// <c>SetDriverMicrostepping(drive, 16, true)</c> for every drive before config.g
    /// </remarks>
    [Test]
    public async Task M350ReportsTheStateEveryDriveStartsIn()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.WithoutDriveSettings);

        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(),
                    Is.EqualTo("Microstepping - X:16(on), Y:16(on), Z:16(on), E:16(on)"));
    }
}
