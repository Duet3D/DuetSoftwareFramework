using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>move.compensation.probeGrid, read in one go so a scenario can compare it whole</summary>
internal sealed record GridSnapshot(char Axis0, char Axis1, float Min0, float Max0, float Min1, float Max1,
                                    float Spacing0, float Spacing1, float Radius);

/// <summary>M557, the probe grid</summary>
[TestFixture]
[GCode("M557")]
public class M557Tests : BenchFixture
{
    /// <summary>Read move.compensation.probeGrid</summary>
    private static Task<GridSnapshot> ReadGridAsync(DcsTestHost host)
        => host.ReadModelAsync(model =>
        {
            ProbeGrid grid = model.Move.Compensation.ProbeGrid;
            return new GridSnapshot(grid.Axes[0], grid.Axes[1], grid.Mins[0], grid.Maxs[0], grid.Mins[1], grid.Maxs[1],
                            grid.Spacings[0], grid.Spacings[1], grid.Radius);
        });

    /// <summary>Spare drivers of <see cref="Machines.DriverBoard"/> for the axes past Z, which nothing moves</summary>
    private const byte ExtraDriver0 = 3, ExtraDriver1 = 4, ExtraDriver2 = 5;

    /// <summary>X, Y and Z plus three more axes with the given letters</summary>
    private static Task<Bench> StartWithExtraAxesAsync(string letters)
        => Bench.StartAsync(Machines.Xyz,
                            $"M584 {letters[0]}{Expansion.Driver(Machines.DriverBoard, ExtraDriver0)} " +
                            $"{letters[1]}{Expansion.Driver(Machines.DriverBoard, ExtraDriver1)} " +
                            $"{letters[2]}{Expansion.Driver(Machines.DriverBoard, ExtraDriver2)}");

    /// <summary>The grid every refusal scenario defines first, so it can tell whether the refused code touched it</summary>
    private const int BaselineMax = 20, BaselineSpacing = 10;

    /// <summary>RRF's report of <see cref="BaselineMax"/> and <see cref="BaselineSpacing"/></summary>
    private const string BaselineReport = "Grid: X0.0:20.0, Y0.0:20.0, radius -1.0, X spacing 10.0, Y spacing 10.0, 9 points";

    /// <summary>
    /// M557 with axis ranges and S spacing defines the default probe grid, reported under
    /// move.compensation.probeGrid; the P point-count form derives the spacing from the range
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid in GCodes6.cpp writes the default grid; with P the spacing becomes
    /// (max - min)/(P - 1) * 0.9999. OM fields from the GridDefinition tables in
    /// Movement/BedProbing/Grid.cpp (mins, maxs, spacings at 1 decimal place, radius -1 for a
    /// rectangular grid). Regression case m557-define-probe-grid
    /// </remarks>
    [Test]
    public async Task M557DefinesProbeGrid()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M557 X20:180 Y20:180 S40");
        char axis0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Axes[0]);
        char axis1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Axes[1]);
        double min0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Mins[0]);
        double max0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Maxs[0]);
        double min1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Mins[1]);
        double max1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Maxs[1]);
        double spacing0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[0]);
        double spacing1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[1]);
        double radius = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Radius);
        Assert.Multiple(() =>
        {
            Assert.That(axis0, Is.EqualTo('X'), "M557 sets move.compensation.probeGrid.axes[0] (RRF GCodes::DefineGrid)");
            Assert.That(axis1, Is.EqualTo('Y'), "M557 sets move.compensation.probeGrid.axes[1]");
            Assert.That(min0, Is.EqualTo(20), "M557 X range sets move.compensation.probeGrid.mins[0]");
            Assert.That(max0, Is.EqualTo(180), "M557 X range sets move.compensation.probeGrid.maxs[0]");
            Assert.That(min1, Is.EqualTo(20), "M557 Y range sets move.compensation.probeGrid.mins[1]");
            Assert.That(max1, Is.EqualTo(180), "M557 Y range sets move.compensation.probeGrid.maxs[1]");
            Assert.That(spacing0, Is.EqualTo(40), "M557 S sets move.compensation.probeGrid.spacings[0]");
            Assert.That(spacing1, Is.EqualTo(40), "M557 S sets move.compensation.probeGrid.spacings[1]");
            Assert.That(radius, Is.EqualTo(-1), "a rectangular M557 grid keeps move.compensation.probeGrid.radius = -1");
        });

        string report = await host.ExecuteCodeAsync("M557");
        Assert.That(report.Trim(), Is.EqualTo("Grid: X20.0:180.0, Y20.0:180.0, radius -1.0, X spacing 40.0, Y spacing 40.0, 25 points"),
                    "bare M557 reports the grid in RRF's format (GridDefinition::PrintParameters)");

        await host.ExecuteCodeAsync("M557 X0:100 Y0:100 P5");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[0]), Is.EqualTo(100.0 / 4.0 * 0.9999).Within(1e-3),
                    "M557 P derives move.compensation.probeGrid.spacings[0] as range/(P-1) * 0.9999 (RRF DefineGrid)");
    }

    /// <summary>
    /// Before any M557 the grid is RRF's undefined one: X and Y, mins 0, maxs -1, spacings 0 and
    /// radius -1, and a bare M557 says no grid is defined
    /// </summary>
    /// <remarks>
    /// The GridDefinition constructor in Movement/BedProbing/Grid.cpp. The regression references of
    /// m557-define-probe-grid and m557-define-radial-grid start from exactly these values; radius
    /// -1 is what DuetAPI's ProbeGrid does not start with
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ProbeGridStartsUndefined()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That(await ReadGridAsync(bench.Host), Is.EqualTo(new GridSnapshot('X', 'Y', 0, -1, 0, -1, 0, 0, -1)),
                    "move.compensation.probeGrid starts as RRF's GridDefinition() (radius -1)");
    }

    /// <summary>A bare M557 before any grid is defined says so, successfully</summary>
    /// <remarks>
    /// GCodes::DefineGrid: with no axis, R, S or P it prints the grid if GridDefinition::IsValid,
    /// else "grid is not defined", returning ok either way
    /// </remarks>
    [Test]
    public async Task BareM557ReportsThatNoGridIsDefined()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M557");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error").And.Not.StartWith("Warning"), "the report succeeds");
            Assert.That(reply, Does.Contain("grid is not defined").IgnoreCase, "and says there is no grid");
        });
    }

    /// <summary>
    /// S with two values sets each axis' spacing; with no S or P both spacings are the 20mm default
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid reads S with TryGetFloatArray(..., 2, ..., doPad = true), so one value
    /// serves both axes and two set one each; spacings start at DefaultGridSpacing (20, Configuration.h).
    /// The S50:60 line is the wiki's own M557 example
    /// </remarks>
    [Test]
    public async Task M557SetsEachAxisSpacingOrDefaultsIt()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        Assert.That((await host.ExecuteCodeAsync("M557 X0:100 Y0:120 S50:60")).Trim(), Is.Empty, "M557 with a spacing pair succeeds silently");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('X', 'Y', 0, 100, 0, 120, 50, 60, -1)),
                    "S50:60 sets spacings[0] to 50 and spacings[1] to 60");
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X0.0:100.0, Y0.0:120.0, radius -1.0, X spacing 50.0, Y spacing 60.0, 9 points"));

        Assert.That((await host.ExecuteCodeAsync("M557 X0:100 Y0:100")).Trim(), Is.Empty, "M557 with ranges alone succeeds");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('X', 'Y', 0, 100, 0, 100, 20, 20, -1)),
                    "with neither S nor P the spacing is RRF's DefaultGridSpacing of 20mm");
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X0.0:100.0, Y0.0:100.0, radius -1.0, X spacing 20.0, Y spacing 20.0, 36 points"));
    }

    /// <summary>
    /// P with one value is the point count of both axes, with two the count of each; the spacing is
    /// derived from the range, P wins over S, and the report shows the counts asked for
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid: P is read with TryGetUIArray(..., doPad = true) and, when present, S is not
    /// read at all; each spacing becomes (max - min)/(P - 1) * 0.9999. Regression cases
    /// m557-grid-point-counts (P3, then P3:5 reporting "15 points") and m557-report-grid (P2:2
    /// reporting "X spacing 20.0, ... 4 points"). RRF's object model gives the spacings to one
    /// decimal place (10.0), DSF's holds the value itself (9.999), per rrf-differences.md section 3
    /// </remarks>
    [Test]
    public async Task M557DerivesTheSpacingFromPointCounts()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M557 X0:20 Y0:20 P3");
        GridSnapshot square = await ReadGridAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(square.Spacing0, Is.EqualTo(10 * 0.9999).Within(1e-4), "P3 over 20mm gives spacings[0] = 20/2 * 0.9999");
            Assert.That(square.Spacing1, Is.EqualTo(10 * 0.9999).Within(1e-4), "one P value serves both axes");
        });

        await host.ExecuteCodeAsync("M557 X0:20 Y0:40 P3:5");
        GridSnapshot pair = await ReadGridAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(pair.Max1, Is.EqualTo(40), "Y0:40 sets maxs[1]");
            Assert.That(pair.Spacing0, Is.EqualTo(10 * 0.9999).Within(1e-4), "P3:5 gives X 3 points over 20mm");
            Assert.That(pair.Spacing1, Is.EqualTo(10 * 0.9999).Within(1e-4), "and Y 5 points over 40mm");
        });
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X0.0:20.0, Y0.0:40.0, radius -1.0, X spacing 10.0, Y spacing 10.0, 15 points"),
                    "the report matches the m557-grid-point-counts reference");

        await host.ExecuteCodeAsync("M557 X0:20 Y0:20 P2:2");
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X0.0:20.0, Y0.0:20.0, radius -1.0, X spacing 20.0, Y spacing 20.0, 4 points"),
                    "the report matches the m557-report-grid reference");

        await host.ExecuteCodeAsync("M557 X0:20 Y0:20 P3 S5");
        Assert.That((await ReadGridAsync(host)).Spacing0, Is.EqualTo(10 * 0.9999).Within(1e-4),
                    "with both P and S, P sets the spacing and S is ignored (RRF reads S only when P is absent)");
    }

    /// <summary>
    /// A 31 x 31 grid, the 961 points RRF allows on the MB6HC, is accepted
    /// </summary>
    /// <remarks>
    /// MaxGridProbePoints in Config/Configuration.h is 961 on SAME70 builds (the MB6HC the regression
    /// references are recorded on) and 441 on the rest; the wiki's M557 notes give both. DSF caps the
    /// grid at 441
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M557AcceptsTheMb6hcGridPointLimit()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M557 X0:300 Y0:300 S10");
        Assert.That(reply.Trim(), Is.Empty, "31 x 31 = 961 points is within RRF's MB6HC limit");
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X0.0:300.0, Y0.0:300.0, radius -1.0, X spacing 10.0, Y spacing 10.0, 961 points"));
        
        Assert.That((await host.ExecuteCodeAsync("M557 X0:200 Y0:200 P41:2")).Trim(), Is.Empty, "max points in axis0 is accepted");
        Assert.That((await host.ExecuteCodeAsync("M557 X0:200 Y0:200 P2:41")).Trim(), Is.Empty, "max points in axis1 is accepted");
    }

    /// <summary>
    /// The radial form inscribes an X/Y grid in a circle: with S the half-width is the largest
    /// multiple of the spacing inside R - 0.1, without S the spacing is the 20mm default
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid, the seenR branch: effective radius floorf((R - 0.1)/S) * S, mins -effective,
    /// maxs effective + 0.1, the radius recorded. Regression case m557-define-radial-grid (R50 S25:
    /// mins -25.0, maxs 25.1, radius 50.0, spacings 25.0)
    /// </remarks>
    [Test]
    public async Task M557DefinesARadialGridFromASpacing()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        Assert.That((await host.ExecuteCodeAsync("M557 R50 S25")).Trim(), Is.Empty, "M557 R S succeeds silently");
        GridSnapshot withSpacing = await ReadGridAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That((withSpacing.Axis0, withSpacing.Axis1), Is.EqualTo(('X', 'Y')), "a radial grid is over X and Y");
            Assert.That(withSpacing.Min0, Is.EqualTo(-25).Within(1e-4), "mins[0] is -floor(49.9/25)*25");
            Assert.That(withSpacing.Max0, Is.EqualTo(25.1).Within(1e-4), "maxs[0] is that plus 0.1");
            Assert.That(withSpacing.Min1, Is.EqualTo(-25).Within(1e-4), "mins[1] likewise");
            Assert.That(withSpacing.Max1, Is.EqualTo(25.1).Within(1e-4), "maxs[1] likewise");
            Assert.That(withSpacing.Spacing0, Is.EqualTo(25), "spacings[0] is S");
            Assert.That(withSpacing.Spacing1, Is.EqualTo(25), "spacings[1] is S");
            Assert.That(withSpacing.Radius, Is.EqualTo(50), "radius is R");
        });
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X-25.0:25.1, Y-25.0:25.1, radius 50.0, X spacing 25.0, Y spacing 25.0, 9 points"));

        Assert.That((await host.ExecuteCodeAsync("M557 R50")).Trim(), Is.Empty, "M557 R alone succeeds");
        GridSnapshot withoutSpacing = await ReadGridAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(withoutSpacing.Spacing0, Is.EqualTo(20), "without S spacings[0] is RRF's DefaultGridSpacing of 20mm");
            Assert.That(withoutSpacing.Spacing1, Is.EqualTo(20), "and spacings[1] too");
            Assert.That(withoutSpacing.Min0, Is.EqualTo(-40).Within(1e-4), "mins[0] is -floor(49.9/20)*20");
            Assert.That(withoutSpacing.Max0, Is.EqualTo(40.1).Within(1e-4), "maxs[0] is that plus 0.1");
            Assert.That(withoutSpacing.Min1, Is.EqualTo(-40).Within(1e-4), "mins[1] likewise");
            Assert.That(withoutSpacing.Max1, Is.EqualTo(40.1).Within(1e-4), "maxs[1] likewise");
        });
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X-40.0:40.1, Y-40.0:40.1, radius 50.0, X spacing 20.0, Y spacing 20.0, 25 points"));
    }

    /// <summary>
    /// The radial form with point counts spaces them over R - 0.1, narrowing an axis by
    /// sqrt(1 - 1/(n-1)²) when the other axis has an even count n and so no row through the centre
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid, the seenR branch with P: effective radius R - 0.1, scaled when the other
    /// axis' count is even, spacing 2 * effective/(P - 1). With P5:4, X is narrowed (Y has 4 points)
    /// to 49.9 * sqrt(8/9) = 47.046 and spaced 94.092/4; Y keeps 49.9 (X has 5) and is spaced 99.8/3
    /// </remarks>
    [Test]
    public async Task M557DefinesARadialGridFromPointCounts()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        Assert.That((await host.ExecuteCodeAsync("M557 R50 P5:4")).Trim(), Is.Empty, "M557 R P succeeds silently");
        float narrowed = 49.9f * MathF.Sqrt(1.0f - 1.0f / 9.0f);
        GridSnapshot grid = await ReadGridAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(grid.Min0, Is.EqualTo(-narrowed).Within(1e-2), "X is narrowed because Y has an even count");
            Assert.That(grid.Max0, Is.EqualTo(narrowed + 0.1f).Within(1e-2), "maxs[0] is the narrowed radius plus 0.1");
            Assert.That(grid.Spacing0, Is.EqualTo(2 * narrowed / 4).Within(1e-2), "5 points along X");
            Assert.That(grid.Min1, Is.EqualTo(-49.9).Within(1e-3), "Y is not narrowed because X has an odd count");
            Assert.That(grid.Max1, Is.EqualTo(50.0).Within(1e-3), "maxs[1] is 49.9 + 0.1");
            Assert.That(grid.Spacing1, Is.EqualTo(2 * 49.9 / 3).Within(1e-3), "4 points along Y");
            Assert.That(grid.Radius, Is.EqualTo(50), "radius is R");
        });
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X-47.0:47.1, Y-49.9:50.0, radius 50.0, X spacing 23.5, Y spacing 33.3, 20 points"));
    }

    /// <summary>
    /// Ranges and a radius together give a rectangular grid that also records the radius, so that
    /// only the points inside both are probed
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid takes the axes branch when ranges are given and passes R to
    /// GridDefinition::Set unchanged; GridDefinition::IsInRadius then limits probing. The wiki's
    /// M557 notes: "If you define both, the probing area will be the intersection"
    /// </remarks>
    [Test]
    public async Task M557WithRangesAndARadiusKeepsBoth()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        Assert.That((await host.ExecuteCodeAsync("M557 X0:100 Y0:100 R50 S20")).Trim(), Is.Empty, "M557 with ranges and R succeeds");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('X', 'Y', 0, 100, 0, 100, 20, 20, 50)),
                    "the ranges are kept as given and the radius recorded");
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: X0.0:100.0, Y0.0:100.0, radius 50.0, X spacing 20.0, Y spacing 20.0, 36 points"));
    }

    /// <summary>
    /// Since RRF 3.3 a grid may be over any two axes but Z: U/V, then U/W, are each recorded and
    /// reported by their letters, and a radial grid goes back to X and Y
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid scans every visible axis letter. Regression case m557-grid-extra-axes
    /// ("Grid: U0.0:20.0, V0.0:20.0, radius -1.0, U spacing 10.0, V spacing 10.0, 9 points", then
    /// U/W). The letters start as X and Y on every M557, so the R form is over X and Y (wiki: "When
    /// using Raaa to define a radius this will default to X-Y")
    /// </remarks>
    [Test]
    public async Task M557DefinesAGridOverExtraAxes()
    {
        await using Bench bench = await StartWithExtraAxesAsync("UVW");
        DcsTestHost host = bench.Host;

        Assert.That((await host.ExecuteCodeAsync($"M557 U0:{BaselineMax} V0:{BaselineMax} S{BaselineSpacing}")).Trim(), Is.Empty,
                    "a U/V grid is accepted");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('U', 'V', 0, BaselineMax, 0, BaselineMax, BaselineSpacing, BaselineSpacing, -1)));
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: U0.0:20.0, V0.0:20.0, radius -1.0, U spacing 10.0, V spacing 10.0, 9 points"));

        Assert.That((await host.ExecuteCodeAsync($"M557 U0:{BaselineMax} W0:{BaselineMax} S{BaselineSpacing}")).Trim(), Is.Empty,
                    "a U/W grid is accepted");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('U', 'W', 0, BaselineMax, 0, BaselineMax, BaselineSpacing, BaselineSpacing, -1)));
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: U0.0:20.0, W0.0:20.0, radius -1.0, U spacing 10.0, W spacing 10.0, 9 points"));

        await host.ExecuteCodeAsync("M557 R50 S25");
        GridSnapshot radial = await ReadGridAsync(host);
        Assert.That((radial.Axis0, radial.Axis1), Is.EqualTo(('X', 'Y')), "a radial grid after a U/W one is over X and Y again");
    }

    /// <summary>
    /// The rotary letters work as well: A/B, then A/C, and an X/A pair
    /// </summary>
    /// <remarks>
    /// As <see cref="M557DefinesAGridOverExtraAxes"/>. Regression case m557-grid-rotary-axes; the
    /// X-A pair is the wiki's own example of an arbitrary axis pair
    /// </remarks>
    [Test]
    public async Task M557DefinesAGridOverRotaryAxes()
    {
        await using Bench bench = await StartWithExtraAxesAsync("ABC");
        DcsTestHost host = bench.Host;

        Assert.That((await host.ExecuteCodeAsync($"M557 A0:{BaselineMax} B0:{BaselineMax} S{BaselineSpacing}")).Trim(), Is.Empty,
                    "an A/B grid is accepted");
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: A0.0:20.0, B0.0:20.0, radius -1.0, A spacing 10.0, B spacing 10.0, 9 points"));

        Assert.That((await host.ExecuteCodeAsync($"M557 A0:{BaselineMax} C0:{BaselineMax} S{BaselineSpacing}")).Trim(), Is.Empty,
                    "an A/C grid is accepted");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('A', 'C', 0, BaselineMax, 0, BaselineMax, BaselineSpacing, BaselineSpacing, -1)));
        Assert.That((await host.ExecuteCodeAsync("M557")).Trim(),
                    Is.EqualTo("Grid: A0.0:20.0, C0.0:20.0, radius -1.0, A spacing 10.0, C spacing 10.0, 9 points"));

        Assert.That((await host.ExecuteCodeAsync($"M557 X0:{BaselineMax} A0:{2 * BaselineMax} S{BaselineSpacing}")).Trim(), Is.Empty,
                    "an X/A grid is accepted");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(new GridSnapshot('X', 'A', 0, BaselineMax, 0, 2 * BaselineMax, BaselineSpacing, BaselineSpacing, -1)));
    }

    /// <summary>
    /// U and Y make U the first axis of the grid, however the code orders them, so that U behaves
    /// like X on an IDEX machine
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid finds Y before U because it scans in axis order, then swaps the letters,
    /// ranges and spacings when it has Y then U
    /// </remarks>
    [Test]
    public async Task M557PutsUBeforeY()
    {
        await using Bench bench = await StartWithExtraAxesAsync("UVW");
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync($"M557 Y0:{2 * BaselineMax} U0:{BaselineMax} S{BaselineSpacing}:{2 * BaselineSpacing}");
        Assert.That(await ReadGridAsync(host),
                    Is.EqualTo(new GridSnapshot('U', 'Y', 0, BaselineMax, 0, 2 * BaselineMax, 2 * BaselineSpacing, BaselineSpacing, -1)),
                    "U comes first, taking its own range; the spacings follow the axes they were swapped with");
    }

    /// <summary>
    /// Letters of axes the machine does not have are not M557 parameters: without them and without
    /// S, P or R the code is a report, and with S it is refused for lacking a range
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid only looks for the letters of the visible axes, so U and V on an X/Y/Z
    /// machine are never seen; what is left is the report form, or S alone, which is refused
    /// </remarks>
    [Test]
    public async Task M557IgnoresLettersOfAxesTheMachineLacks()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;
        await host.ExecuteCodeAsync($"M557 X0:{BaselineMax} Y0:{BaselineMax} S{BaselineSpacing}");
        GridSnapshot before = await ReadGridAsync(host);

        Assert.That((await host.ExecuteCodeAsync($"M557 U0:{BaselineMax} V0:{BaselineMax}")).Trim(), Is.EqualTo(BaselineReport),
                    "with no other parameter it reports the grid");
        Assert.That(await host.ExecuteCodeAsync($"M557 U0:{BaselineMax} V0:{BaselineMax} S{BaselineSpacing}"), Does.StartWith("Error"),
                    "with S it is refused, having no ranges");
        Assert.That(await ReadGridAsync(host), Is.EqualTo(before), "neither touches the grid");
    }

    /// <summary>Codes M557 refuses before it reaches the grid, in RRF as in DSF</summary>
    private static readonly object[] RefusedBeforeTheGrid =
    [
        new object[] { $"M557 X0:{BaselineMax} Z0:{BaselineMax} S{BaselineSpacing}", "Z is not a grid axis" },
        new object[] { $"M557 X0:{BaselineMax} S{BaselineSpacing}", "one axis range alone" },
        new object[] { $"M557 S{BaselineSpacing}", "a spacing without ranges or radius" },
        new object[] { "M557 P3", "a point count without ranges or radius" },
        new object[] { $"M557 R0 S{BaselineSpacing}", "a zero radius without ranges" },
        new object[] { "M557 R-10", "a negative radius without ranges" },
        new object[] { $"M557 X0:{BaselineMax} Y0:{BaselineMax} S1:2:3", "three spacings" }
    ];

    /// <summary>
    /// Codes GridDefinition::Set refuses. RRF writes these into the grid before it checks them, which
    /// leaves it holding the refused values and invalid; DSF checks first and keeps the grid it had
    /// </summary>
    private static readonly object[] RefusedByTheGridCheck =
    [
        new object[] { $"M557 X{BaselineMax}:0 Y0:{BaselineMax} S{BaselineSpacing}", "a minimum above the maximum" },
        new object[] { $"M557 X0:0.5 Y0:{BaselineMax} S0.2", "a range under 1mm" },
        new object[] { $"M557 X0:{BaselineMax} Y0:{BaselineMax} S0.05", "a spacing under 0.1mm" },
        new object[] { "M557 X0:200 Y0:10 S4", "51 points along the first axis" },
        new object[] { "M557 X0:200 Y0:200 S5", "41 x 41 points" },
        new object[] { $"M557 X0:{BaselineMax} Y0:{BaselineMax} P0", "no points" },
        new object[] { $"M557 X0:{BaselineMax} Y0:{BaselineMax} P1", "one point" },
        new object[] { $"M557 X0:{BaselineMax} Y0:{BaselineMax} P1:3", "one point along the first axis" },
        new object[] { $"M557 X0:{BaselineMax} Y0:{BaselineMax} P3:1", "one point along the second axis" },
        new object[] { "M557 R50 P3:1", "one point along the second axis of a circle" }
    ];

    /// <summary>
    /// A refused M557 is an error and leaves the grid it found, in the object model and in the
    /// report
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid returns these errors before calling GridDefinition::Set: "Z axis is not
    /// allowed", "specify zero or two axes", "specify at least radius or two axis ranges", "radius must
    /// be positive", and the GCodeBuffer::TryGetFloatArray length error
    /// </remarks>
    [TestCaseSource(nameof(RefusedBeforeTheGrid))]
    public async Task M557RefusedBeforeTheGridLeavesItIntact(string code, string why)
    {
        await using Bench bench = await StartXyzAsync();
        await AssertRefusedAndGridIntactAsync(bench.Host, code, why);
    }

    /// <summary>
    /// A grid definition that cannot be probed is an error and leaves the grid M557 found
    /// </summary>
    /// <remarks>
    /// RRF's GridDefinition::Set stores the new values and then CheckValidity marks the grid invalid
    /// (GridDefinition::PrintError: "Spacing too small", "X range too small", "Too many grid
    /// points"), so after the refusal RRF's object model shows the refused values and a bare M557
    /// says no grid is defined. Every other refusal of M557, and of configuration codes generally,
    /// leaves what was there, so DSF does the same here. One point along an axis is refused where RRF
    /// divides the range by P - 1 and accepts an infinite spacing (rrf-differences.md section 6.1)
    /// </remarks>
    [TestCaseSource(nameof(RefusedByTheGridCheck))]
    public async Task M557RefusedByTheGridCheckLeavesItIntact(string code, string why)
    {
        await using Bench bench = await StartXyzAsync();
        await AssertRefusedAndGridIntactAsync(bench.Host, code, why);
    }

    /// <summary>
    /// More than two axes is refused, leaving the grid
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid means to refuse a third axis ("mesh leveling expects exactly two axes") but
    /// tests axesSeenCount &gt; 2 before counting it, so the third range overwrites the second axis'
    /// and its letter is written past the end of axesLetters[2]. DSF refuses, as the message intends
    /// </remarks>
    [Test]
    public async Task M557RefusesThreeAxes()
    {
        await using Bench bench = await StartWithExtraAxesAsync("UVW");
        await AssertRefusedAndGridIntactAsync(bench.Host, $"M557 X0:{BaselineMax} Y0:{BaselineMax} U0:{BaselineMax} S{BaselineSpacing}", "three axes");
    }

    /// <summary>
    /// A radius between 0 and 1 is refused, ranges or not
    /// </summary>
    /// <remarks>
    /// GridDefinition::CheckValidity requires radius &lt; 0 or radius &gt;= 1, and PrintError reports
    /// "Bad radius" when nothing else is wrong. (RRF then also leaves the grid holding the refused
    /// values; see <see cref="M557RefusedByTheGridCheckLeavesItIntact"/>)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M557RefusesARadiusUnderOne()
    {
        await using Bench bench = await StartXyzAsync();
        await AssertRefusedAndGridIntactAsync(bench.Host, "M557 X0:100 Y0:100 R0.5 S20", "a radius of 0.5");
    }

    /// <summary>
    /// Define <see cref="BaselineReport"/>'s grid, run a code that must be refused, and check the grid
    /// and its report did not change
    /// </summary>
    private static async Task AssertRefusedAndGridIntactAsync(DcsTestHost host, string code, string why)
    {
        Assert.That((await host.ExecuteCodeAsync($"M557 X0:{BaselineMax} Y0:{BaselineMax} S{BaselineSpacing}")).Trim(), Is.Empty,
                    "the baseline grid is accepted");
        GridSnapshot before = await ReadGridAsync(host);

        string reply = await host.ExecuteCodeAsync(code);
        GridSnapshot after = await ReadGridAsync(host);
        string report = await host.ExecuteCodeAsync("M557");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"), $"{code} is refused: {why}");
            Assert.That(after, Is.EqualTo(before), "move.compensation.probeGrid keeps the grid it had");
            Assert.That(report.Trim(), Is.EqualTo(BaselineReport), "and M557 still reports it");
        });
    }
}
