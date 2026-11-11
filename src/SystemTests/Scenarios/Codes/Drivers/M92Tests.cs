using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M92, steps per mm</summary>
[TestFixture]
[GCode("M92")]
public class M92Tests : BenchFixture
{
    /// <summary>
    /// M92 sets the steps per millimetre of every axis it names, in one request carrying all of them
    /// </summary>
    /// <remarks><c>testcases/drivers/m92-set-steps-per-mm.yaml</c></remarks>
    [Test]
    public async Task M92SetsStepsPerMm()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M92 X100 Y100 Z800")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent, Has.Count.EqualTo(1), "three axes on one board are one request");
        Assert.That(sent[0].Payload.Length,
                    Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b111 }.GetActualDataLength()),
                    "carrying three drives' values and stopping there");

        (byte board, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping message) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(message.DriversToUpdate, Is.EqualTo(0b111), "drivers 0, 1 and 2 of that board");
            Assert.That(message.Values[0].GetStepsPerUnit(), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(message.Values[1].GetStepsPerUnit(), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(message.Values[2].GetStepsPerUnit(), Is.EqualTo(800.0f).Within(1e-3));
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm), Is.EqualTo(800.0f).Within(1e-3));
        });
    }

    /// <summary>
    /// M92 S quotes the values at one microstepping and the firmware rescales them to the one in use
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m92-steps-at-microstepping.yaml</c>: at x32, values quoted at x16 are
    /// stored doubled
    /// </remarks>
    [Test]
    public async Task M92SRescalesToTheMicrosteppingInUse()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M350 X32 Y32 Z32 I1");

        await bench.Host.ExecuteCodeAsync("M92 X80 Y80 Z400 S16");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(160.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm), Is.EqualTo(160.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm), Is.EqualTo(800.0f).Within(1e-3));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M92")).TrimEnd(),
                    Is.EqualTo("Steps/mm: X: 160.000, Y: 160.000, Z: 800.000, E: 420.000"),
                    "and the report quotes what was stored (m92-report-steps-per-mm.yaml)");
    }

    /// <summary>
    /// M92 refuses a value of zero, quoting the column it stood in
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-missing-parameters-drivers.yaml</c>: steps per millimetre is read
    /// with GetPositiveFValue, so zero is refused rather than stored and used to plan every later move
    /// </remarks>
    [Test]
    public async Task M92RefusesZeroStepsPerMm()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M92 X0");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"),
                        "0 steps per mm must be an error");
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(),
                        Is.Empty, "no CAN messages should be sent");
        });
    }

    /// <summary>
    /// A value refused part-way through leaves the drives read before it stored, and tells no board
    /// </summary>
    /// <remarks>
    /// RepRapFirmware stores each drive as it reads it and collects the drives to forward, sending
    /// the one remote update after the whole line has been read (GCodes2.cpp case 92, where
    /// UpdateRemoteStepsPerMmAndMicrostepping is reached only past the loop). <c>M92 X200 Y0</c>
    /// therefore leaves X at 200 in the object model with the boards still stepping it at 80, which
    /// is the firmware's own outcome rather than a choice made here
    /// </remarks>
    [Test]
    public async Task M92KeepsWhatItReadWhenAValueIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M92 X200 Y0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(200.0f).Within(1e-3),
                        "X was read before the refusal, so it stands");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm), Is.EqualTo(80.0f).Within(1e-3),
                        "and Y keeps what it had, because its value was never usable");
        });

        Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(), Is.Empty,
                    "the boards are told once the whole line has been read, so a refusal on it sends nothing "
                    + "(RRF GCodes2.cpp case 92 reaches UpdateRemoteStepsPerMmAndMicrostepping only after the loop)");
    }
}
