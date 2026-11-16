using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Tools.ToolSetup;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>G10, a tool's offsets and temperatures</summary>
[TestFixture]
[GCode("G10")]
public class G10Tests : BenchFixture
{
    /// <summary>G10 with axis letters sets the tool's offsets</summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 10 (GCodes.cpp): each seen axis letter goes to
    /// Tool::SetOffset, reported in tools[].offsets
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G10SetsToolOffsets()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G10 P0 X5 Y-2");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Offsets[0]), Is.EqualTo(5).Within(1e-6), "G10 X sets tools[0].offsets[0] (RRF Tool::SetOffset)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Offsets[1]), Is.EqualTo(-2).Within(1e-6), "G10 Y sets tools[0].offsets[1] (RRF Tool::SetOffset)");
        });

        string reply = (await host.ExecuteCodeAsync("G10 P0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain("Tool 0: offsets"), "bare G10 P reports the offsets (RRF SetOrReportOffsets)");
            Assert.That(reply, Does.Contain("X5.000"), "the reported X offset matches the model (RRF SetOrReportOffsets)");
            Assert.That(reply, Does.Contain("Y-2.000"), "the reported Y offset matches the model (RRF SetOrReportOffsets)");
        });
    }

    /// <summary>G10 with P/R/S sets the tool's temperatures, exactly as M568 does</summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 10 (GCodes.cpp): S and R share the temperature path
    /// with M568, so tools[].active/standby and the heater setpoints follow
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G10SetsToolTemperatures()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G10 P0 S200 R150");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Active[0]), Is.EqualTo(200), "G10 S sets tools[0].active (RRF SetOrReportOffsets code 10)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Standby[0]), Is.EqualTo(150), "G10 R sets tools[0].standby (RRF SetOrReportOffsets code 10)");
            Assert.That(await host.ReadModelAsync(model => model.Heat.Heaters[0]!.Active), Is.EqualTo(200), "the G10 temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
            Assert.That(await host.ReadModelAsync(model => model.Heat.Heaters[0]!.Standby), Is.EqualTo(150), "the G10 temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
        });
    }

    /// <summary>G10 L1 is G10 without L: it sets the tool's offsets</summary>
    /// <remarks>
    /// GCodes2.cpp case 10 sends L1 to SetOrReportOffsets. Reference
    /// g10-tool-offsets-and-temperatures: "G10 L1 P0 X-2.5 Y3.25 Z0.75"
    /// </remarks>
    [Test]
    public async Task G10L1SetsToolOffsets()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, "M563 P0 D0");

        string reply = await bench.Host.ExecuteCodeAsync("G10 L1 P0 X-2.5 Y3.25 Z0.75");
        float[] offsets = await bench.Host.ReadModelAsync(model => model.Tools[0]!.Offsets.Take(3).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "G10 L1 replies with nothing");
            Assert.That(offsets, Is.EqualTo(new[] { -2.5f, 3.25f, 0.75f }).Within(1e-6f), "tools[0].offsets");
        });
    }

    #region Workplace coordinates (L2 and L20)

    /// <summary>A workplace's origin, by axis, numbered from 1 as G10 L2 P numbers them</summary>
    private static Task<float[]> ReadWorkplaceAsync(DcsTestHost host, int workplace)
        => host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.WorkplaceOffsets[workplace - 1]).ToArray());

    /// <summary>G10 L2 sets a workplace's origin and replies with nothing</summary>
    /// <remarks>
    /// GCodes3.cpp GetSetWorkplaceCoordinates. Reference g10-set-workplace-offset: "G10 L2 P2 X10
    /// Y10 Z0" sets move.axes[*].workplaceOffsets[1]
    /// </remarks>
    [Test]
    public async Task G10L2SetsAWorkplaceOrigin()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string reply = await bench.Host.ExecuteCodeAsync("G10 L2 P2 X10 Y10 Z-1.5");
        float[] origin = await ReadWorkplaceAsync(bench.Host, 2);
        float[] first = await ReadWorkplaceAsync(bench.Host, 1);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "G10 L2 replies with nothing");
            Assert.That(origin, Is.EqualTo(new[] { 10f, 10f, -1.5f }).Within(1e-6f), "move.axes[*].workplaceOffsets[1]");
            Assert.That(first, Is.EqualTo(new[] { 0f, 0f, 0f }), "workplace 1 is untouched");
        });
    }

    /// <summary>G10 L2 with no axis reports the workplace's origin</summary>
    /// <remarks>Reference g10-set-workplace-offset: "G10 L2 P2" answers "Origin of workplace 2: X10.00 Y10.00 Z0.00"</remarks>
    [Test]
    public async Task G10L2WithoutAxesReportsTheWorkplace()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync("G10 L2 P2 X10 Y10 Z0");

        Assert.That((await bench.Host.ExecuteCodeAsync("G10 L2 P2")).Trim(), Is.EqualTo("Origin of workplace 2: X10.00 Y10.00 Z0.00"));
    }

    /// <summary>G10 L20 sets the origin so that the current position has the given coordinates in the workplace</summary>
    /// <remarks>
    /// GCodes3.cpp GetSetWorkplaceCoordinates with compute set: currentUserPosition minus the value.
    /// Reference g10-set-workplace-offset: after G92 X20 Y5 Z0, "G10 L20 P2 X0 Y0 Z0" makes
    /// workplace 2's origin X20 Y5 Z0
    /// </remarks>
    [Test]
    public async Task G10L20MakesTheCurrentPositionTheGivenCoordinates()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync("G92 X20 Y5 Z0");

        Assert.That((await bench.Host.ExecuteCodeAsync("G10 L20 P2 X0 Y0 Z0")).Trim(), Is.Empty, "G10 L20 replies with nothing");
        Assert.That((await bench.Host.ExecuteCodeAsync("G10 L2 P2")).Trim(), Is.EqualTo("Origin of workplace 2: X20.00 Y5.00 Z0.00"),
                    "the origin is where the machine is, less the coordinates given");
    }

    /// <summary>P0, or no P, is the current workplace, and changing its origin moves the reported position</summary>
    /// <remarks>
    /// GetSetWorkplaceCoordinates takes cs 0 as currentCoordinateSystem + 1. Reference
    /// g10-offset-extra-axes: setting workplace 1's origin moves move.axes[].userPosition the other way
    /// </remarks>
    [TestCase("G10 L2 X1.5")]
    [TestCase("G10 L2 P0 X1.5")]
    public async Task G10L2WithoutAWorkplaceAddressesTheCurrentOne(string code)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        await bench.Host.ExecuteCodeAsync(code);
        float origin = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].WorkplaceOffsets[0]);
        float? userPosition = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition);
        Assert.Multiple(() =>
        {
            Assert.That(origin, Is.EqualTo(1.5f).Within(1e-6f), "move.axes[0].workplaceOffsets[0], the current workplace's");
            Assert.That(userPosition, Is.EqualTo(-1.5f).Within(1e-6f), "move.axes[0].userPosition is now measured from the new origin");
        });
    }

    /// <summary>A workplace beyond the ninth is refused, and nothing changes</summary>
    /// <remarks>GetSetWorkplaceCoordinates reads P with TryGetLimitedUIValue(..., NumCoordinateSystems + 1)</remarks>
    [Test]
    public async Task G10L2BeyondTheLastWorkplaceIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string reply = await bench.Host.ExecuteCodeAsync("G10 L2 P10 X1");
        float[] current = await ReadWorkplaceAsync(bench.Host, 1);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"), "P10 names no workplace");
            Assert.That(current, Is.EqualTo(new[] { 0f, 0f, 0f }), "and the current workplace is not set in its place");
        });
    }

    /// <summary>An L G10 does not know is refused, and neither a workplace nor a tool changes</summary>
    /// <remarks>GCodes2.cpp case 10 answers any other L with badOrMissingParameter, "Bad or missing parameter"</remarks>
    [Test]
    public async Task G10WithAnUnknownLIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, "M563 P0 D0");

        string reply = (await bench.Host.ExecuteCodeAsync("G10 L3 P0 X1")).TrimEnd();
        float[] workplace = await ReadWorkplaceAsync(bench.Host, 1);
        float toolOffset = await bench.Host.ReadModelAsync(model => model.Tools[0]!.Offsets.FirstOrDefault());
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.EndWith("Bad or missing parameter"), "L3 is no form of G10");
            Assert.That(workplace, Is.EqualTo(new[] { 0f, 0f, 0f }), "no workplace was set");
            Assert.That(toolOffset, Is.Zero, "nor tool 0's offset");
        });
    }

    /// <summary>Under G20 a linear origin is in inches and so is the report; a rotary one stays in degrees</summary>
    /// <remarks>
    /// GetSetWorkplaceCoordinates converts with GetDistance and reports with InverseConvertDistance.
    /// RRF converts a rotary axis too; DSF treats it as a move does (rrf-differences.md)
    /// </remarks>
    [Test]
    public async Task G10L2FollowsTheDistanceUnits()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, $"M584 A{Expansion.Driver(Machines.DriverBoard, 4)}");

        await bench.Host.ExecuteCodeAsync("G20");
        await bench.Host.ExecuteCodeAsync("G10 L2 P2 X1 A90");
        float[] origin = await ReadWorkplaceAsync(bench.Host, 2);
        string report = (await bench.Host.ExecuteCodeAsync("G10 L2 P2")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(origin[0], Is.EqualTo(25.4f).Within(1e-4f), "X is stored in mm");
            Assert.That(origin[3], Is.EqualTo(90f).Within(1e-4f), "A is stored in degrees, unconverted");
            Assert.That(report, Is.EqualTo("Origin of workplace 2: X1.00 Y0.00 Z0.00 A90.00"), "the report is in the units it was set in");
        });
    }

    /// <summary>A hidden axis has no workplace origin to set: G10 L2 naming only it is a report</summary>
    /// <remarks>GetSetWorkplaceCoordinates loops over numVisibleAxes</remarks>
    [Test]
    public async Task G10L2IgnoresHiddenAxes()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, $"M584 U{Expansion.Driver(Machines.DriverBoard, 4)} P3");

        string reply = (await bench.Host.ExecuteCodeAsync("G10 L2 P2 U5")).Trim();
        float hidden = await bench.Host.ReadModelAsync(model => model.Move.Axes[3].WorkplaceOffsets[1]);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.EqualTo("Origin of workplace 2: X0.00 Y0.00 Z0.00"), "nothing was set, so the visible axes are reported");
            Assert.That(hidden, Is.Zero, "move.axes[3].workplaceOffsets[1] is untouched");
        });
    }

    #endregion
}
