using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M561, clearing bed compensation</summary>
[TestFixture]
[GCode("M561")]
public class M561Tests : BenchFixture
{
    /// <summary>Index of Z in move.axes on <see cref="Machines.Xyz"/></summary>
    private const int ZAxis = 2;

    /// <summary>Where the move in <see cref="M561KeepsTheHeadWhereItIs"/> goes, mm</summary>
    private const int MoveXy = 100, MoveZ = 5;

    /// <summary>Z's user position, move.axes[2].userPosition</summary>
    private static Task<float?> ReadUserZAsync(DcsTestHost host)
        => host.ReadModelAsync(model => model.Move.Axes[ZAxis].UserPosition);

    /// <summary>
    /// M561 disables bed compensation: the loaded map is dropped, the compensation type reports
    /// none and the file, live grid and mesh deviation are cleared
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 561 calls GCodes::ClearBedMapping in GCodes6.cpp, which calls
    /// Move::SetIdentityTransform (Move3.cpp): usingMesh is cleared, so move.compensation.type
    /// reports "none" and file, liveGrid and meshDeviation, which exist only while usingMesh, are
    /// null (OM table in Movement/Move.cpp). Regression case m561-clear-bed-transform
    /// </remarks>
    [Test]
    public async Task M561ClearsBedCompensation()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.Mesh),
                    "M375 loads the map M561 clears (RRF GCodes::LoadHeightMap)");

        string clearReply = await host.ExecuteCodeAsync("M561");
        Assert.That(clearReply.Trim(), Is.Empty, "M561 answers silently (RRF GCodes2.cpp case 561)");
        (MoveCompensationType type, string? file, bool hasLiveGrid, bool hasDeviation) = await host.ReadModelAsync(model =>
            (model.Move.Compensation.Type, model.Move.Compensation.File,
             model.Move.Compensation.LiveGrid is not null, model.Move.Compensation.MeshDeviation is not null));
        Assert.Multiple(() =>
        {
            Assert.That(type, Is.EqualTo(MoveCompensationType.None), "M561 sets move.compensation.type back to none (RRF GCodes::ClearBedMapping)");
            Assert.That(file, Is.Null, "M561 clears move.compensation.file");
            Assert.That(hasLiveGrid, Is.False, "M561 clears move.compensation.liveGrid");
            Assert.That(hasDeviation, Is.False, "M561 clears move.compensation.meshDeviation");
        });
    }

    /// <summary>
    /// With the head where the map was loaded, M561 gives back the correction the map had taken off
    /// the user position: Z reads -0.1 with the map and 0 without it
    /// </summary>
    /// <remarks>
    /// GCodes::ActivateHeightmap, on loading, and GCodes::ClearBedMapping, on M561, both recompute
    /// every motion system's user position from the machine position (Move::GetCurrentUserPosition,
    /// then ToolOffsetInverseTransform) so the user position reflects the transform in effect.
    /// Regression case m561-clear-bed-transform: move.axes[2].userPosition goes from -0.03 (the map's
    /// height under the head) to 0
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M561RemovesTheMapFromTheUserPosition()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        Assert.That(await ReadUserZAsync(host), Is.EqualTo(-HeightMapError).Within(1e-4),
                    "with the map loaded move.axes[2].userPosition is the machine Z less the map's height");

        await host.ExecuteCodeAsync("M561");
        Assert.That(await ReadUserZAsync(host), Is.EqualTo(0).Within(1e-4),
                    "M561 makes move.axes[2].userPosition the machine Z again");
    }

    /// <summary>
    /// M561 does not move the head: after a move made with the map, the user position takes up the
    /// correction instead, and the next move is made without it
    /// </summary>
    /// <remarks>
    /// GCodes::ClearBedMapping only recomputes the user position from where the machine is, so a head
    /// lowered onto a map reading +0.1 at Z5 is at machine Z 5.1 and reads Z 5.1 once the map is gone;
    /// moves from then on have no bed transform (Move::SetIdentityTransform clears usingMesh)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M561KeepsTheHeadWhereItIs()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        await host.ExecuteCodeAsync($"G1 X{MoveXy} Y{MoveXy} Z{MoveZ} F{Machines.XySpeed}");
        await bench.WaitForMachinePositionAsync(ZAxis, MoveZ + HeightMapError);

        Assert.That((await host.ExecuteCodeAsync("M561")).Trim(), Is.Empty, "M561 succeeds silently");
        float? userZ = await ReadUserZAsync(host);
        double machineZ = await host.MachinePositionAsync(ZAxis);
        Assert.Multiple(() =>
        {
            Assert.That(machineZ, Is.EqualTo(MoveZ + HeightMapError).Within(1e-3), "M561 does not move Z");
            Assert.That(userZ, Is.EqualTo(MoveZ + HeightMapError).Within(1e-3), "move.axes[2].userPosition takes up the correction the map was applying");
        });

        await host.ExecuteCodeAsync($"G1 Z{MoveZ}");
        await bench.WaitForMachinePositionAsync(ZAxis, MoveZ);
    }

    /// <summary>
    /// The next move after M561 is planned without the map, so Z goes where it is told
    /// </summary>
    /// <remarks>
    /// Move::SetIdentityTransform clears usingMesh, so Move::AxisAndBedTransform no longer adds the
    /// map's height to Z
    /// </remarks>
    [Test]
    public async Task M561StopsTheMapApplyingToMoves()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        await host.ExecuteCodeAsync($"G1 X{MoveXy} Y{MoveXy} Z{MoveZ} F{Machines.XySpeed}");
        await bench.WaitForMachinePositionAsync(ZAxis, MoveZ + HeightMapError);

        await host.ExecuteCodeAsync("M561");
        await host.ExecuteCodeAsync($"G1 Z{2 * MoveZ}");
        await bench.WaitForMachinePositionAsync(ZAxis, 2 * MoveZ);
    }

    /// <summary>
    /// M561 with no compensation in effect succeeds silently and changes nothing
    /// </summary>
    /// <remarks>
    /// GCodes::ClearBedMapping has no precondition: clearing an identity transform leaves it the
    /// identity and the user position as it was
    /// </remarks>
    [Test]
    public async Task M561WithNoCompensationChangesNothing()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;
        float? before = await ReadUserZAsync(host);

        Assert.That((await host.ExecuteCodeAsync("M561")).Trim(), Is.Empty, "M561 succeeds silently");
        (MoveCompensationType type, string? file) = await host.ReadModelAsync(model => (model.Move.Compensation.Type, model.Move.Compensation.File));
        float? after = await ReadUserZAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(type, Is.EqualTo(MoveCompensationType.None), "move.compensation.type stays none");
            Assert.That(file, Is.Null, "move.compensation.file stays null");
            Assert.That(after, Is.EqualTo(before), "the user position is unchanged");
        });
    }

    /// <summary>
    /// M561 clears only the bed transform: axis skew, the taper height and the probe grid survive it
    /// </summary>
    /// <remarks>
    /// Move::SetIdentityTransform ("does not reset axis angle compensation", Move.h) clears the
    /// random probe points, the height map, usingMesh and zShift; tangents and compensateXY (M556),
    /// useTaper and taperHeight (M376, move.compensation.fadeHeight) and GCodes::defaultGrid (M557,
    /// move.compensation.probeGrid) are untouched
    /// </remarks>
    [Test]
    public async Task M561LeavesSkewTaperAndGrid()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;
        const int SkewFactor = 2, SkewLength = 100, TaperHeight = 10, GridMax = 100, GridSpacing = 25;

        await host.ExecuteCodeAsync($"M556 S{SkewLength} X{SkewFactor}");
        await host.ExecuteCodeAsync($"M376 H{TaperHeight}");
        await host.ExecuteCodeAsync($"M557 X0:{GridMax} Y0:{GridMax} S{GridSpacing}");
        await host.ExecuteCodeAsync("M375");

        await host.ExecuteCodeAsync("M561");
        (float tanXY, float? fadeHeight, float gridMax, float gridSpacing) = await host.ReadModelAsync(model =>
            (model.Move.Compensation.Skew.TanXY, model.Move.Compensation.FadeHeight,
             model.Move.Compensation.ProbeGrid.Maxs[0], model.Move.Compensation.ProbeGrid.Spacings[0]));
        Assert.Multiple(() =>
        {
            Assert.That(tanXY, Is.EqualTo((float)SkewFactor / SkewLength).Within(1e-5), "move.compensation.skew.tanXY survives M561");
            Assert.That(fadeHeight, Is.EqualTo(TaperHeight), "move.compensation.fadeHeight survives M561");
            Assert.That(gridMax, Is.EqualTo(GridMax), "move.compensation.probeGrid.maxs[0] survives M561");
            Assert.That(gridSpacing, Is.EqualTo(GridSpacing), "move.compensation.probeGrid.spacings[0] survives M561");
        });
    }
}
