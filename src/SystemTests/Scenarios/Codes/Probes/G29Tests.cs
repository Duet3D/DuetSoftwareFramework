using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using static SystemTests.Scenarios.Codes.Probes.ProbeSetup;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Probes;

/// <summary>G29: probing the grid (S0), and loading (S1) and clearing (S2) the height map</summary>
[TestFixture]
[GCode("G29")]
public class G29Tests : BenchFixture
{
    /// <summary>
    /// G29 S1 loads sys/heightmap.csv and enables mesh compensation. The map is a hand-written v2
    /// file, so no probing hardware is involved
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 29 dispatches S1 to GCodes::LoadHeightMap (GCodes6.cpp), which loads
    /// sys/heightmap.csv by default and activates the map: move.compensation.type reports "mesh"
    /// (Move::GetCompensationTypeString), .file the loaded file, .liveGrid the file's grid and
    /// .meshDeviation the map statistics (Move::LoadHeightMapFromFile, OM tables in
    /// Movement/Move.cpp)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G29S1LoadsHeightMap()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        string loadReply = await host.ExecuteCodeAsync("G29 S1");
        Assert.That(loadReply.Trim(), Is.Empty, "G29 S1 loads the map silently with no probe configured (RRF GCodes::LoadHeightMap)");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.Mesh),
                    "G29 S1 sets move.compensation.type to mesh (RRF Move::GetCompensationTypeString)");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.File), Does.EndWith("heightmap.csv"),
                    "G29 S1 sets move.compensation.file to the loaded map");

        double liveMin0 = await host.ReadModelAsync(model => model.Move.Compensation.LiveGrid!.Mins[0]);
        double liveMax0 = await host.ReadModelAsync(model => model.Move.Compensation.LiveGrid!.Maxs[0]);
        double liveSpacing0 = await host.ReadModelAsync(model => model.Move.Compensation.LiveGrid!.Spacings[0]);
        double meshMean = await host.ReadModelAsync(model => model.Move.Compensation.MeshDeviation!.Mean);
        double meshDeviation = await host.ReadModelAsync(model => model.Move.Compensation.MeshDeviation!.Deviation);
        Assert.Multiple(() =>
        {
            Assert.That(liveMin0, Is.EqualTo(20).Within(1e-3), "move.compensation.liveGrid.mins[0] comes from the file's grid");
            Assert.That(liveMax0, Is.EqualTo(180).Within(1e-3), "move.compensation.liveGrid.maxs[0] comes from the file's grid");
            Assert.That(liveSpacing0, Is.EqualTo(40).Within(1e-3), "move.compensation.liveGrid.spacings[0] comes from the file's grid");
            Assert.That(meshMean, Is.EqualTo(0.1).Within(1e-3), "move.compensation.meshDeviation.mean is the map's mean height (RRF Move::LoadHeightMapFromFile)");
            Assert.That(meshDeviation, Is.EqualTo(0).Within(1e-3), "a uniform map has zero move.compensation.meshDeviation.deviation");
        });
    }

    /// <summary>
    /// G29 S2 disables mesh compensation and drops the loaded map. The map is loaded through M375
    /// first so the clearing is observable independently of the G29 S1 path
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 29 dispatches S2 to GCodes::ClearBedMapping (GCodes6.cpp), after which
    /// move.compensation.type is "none" and .file null (usingMesh cleared, OM tables in
    /// Movement/Move.cpp)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G29S2ClearsLoadedHeightMap()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.Mesh),
                    "M375 loads the map before G29 S2 clears it (RRF GCodes::LoadHeightMap)");

        await host.ExecuteCodeAsync("G29 S2");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.None),
                    "G29 S2 sets move.compensation.type back to none (RRF GCodes::ClearBedMapping)");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.File), Is.Null,
                    "G29 S2 clears move.compensation.file");
    }

    /// <summary>
    /// G29 S0 probes the M557 grid, adopts the height map and saves it, reporting the statistics
    /// </summary>
    /// <remarks>
    /// RRF truth: the gridProbing states (GCodes4.cpp): every reachable grid point is probed, the
    /// map becomes the mesh in use (move.compensation.type "mesh" with meshDeviation, Move.cpp OM
    /// table) and TrySaveHeightMap(DefaultHeightMapFile) saves it, so move.compensation.file names
    /// 0:/sys/heightmap.csv. Each tap is closed like G30's: the input closes, the controller stops
    /// the move, and the input is released again once the tap has been judged so the next tap's
    /// pre-check sees a clear probe
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    [Category("LongRunning")]
    public async Task G29S0ProbesTheGridAndAdoptsTheMap()
    {
        await using Bench bench = await StartBenchAsync(
            ProbeLine + " F300 T6000 R0.5\nG31 K0 Z0.7 P500\nM557 X20:40 Y20:40 S20");

        // G29 S0 needs a homed machine; G92 homes all three axes at a height above the bed
        await bench.Host.ExecuteCodeAsync("G92 X0 Y0 Z10");

        Task<string> gridTask = bench.Host.ExecuteCodeAsync("G29 S0", timeoutMs: 120_000);
        for (int tap = 0; tap < 4; tap++)
        {
            (uint MoveId, (byte Board, byte Driver)[] Drivers) dive =
                await WaitForArmedMoveAsync(bench.CanMaster, tap, $"probing move {tap + 1} of 4 arriving armed");
            bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
            int movesBefore = bench.CanMaster.SbcPackets(SbcRequest.ScheduleMove).Count;
            StopMove(bench.CanMaster, dive);

            if (tap < 3)
            {
                // The travel to the next point is only scheduled once this tap has been judged, so
                // seeing it means the probe can be released without failing the tap
                await bench.CanMaster.WaitUntilAsync(
                    () => bench.CanMaster.SbcPackets(SbcRequest.ScheduleMove).Count > movesBefore,
                    20_000, $"the travel move after tap {tap + 1}");
                bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: false);
            }
        }

        string reply = await gridTask;
        Assert.That(reply, Does.StartWith("4 points probed"),
                    "G29 S0 reports the probed points and statistics (RRF GCodes4.cpp gridProbing report)");

        (MoveCompensationType Type, bool HasDeviation, string? File) compensation = await bench.Host.ReadModelAsync(
            model => (model.Move.Compensation.Type, model.Move.Compensation.MeshDeviation is not null,
                      model.Move.Compensation.File));
        Assert.Multiple(() =>
        {
            Assert.That(compensation.Type, Is.EqualTo(MoveCompensationType.Mesh),
                        "G29 S0 sets move.compensation.type to mesh (RRF Move.cpp OM table, GetCompensationTypeString)");
            Assert.That(compensation.HasDeviation, Is.True,
                        "G29 S0 sets move.compensation.meshDeviation (RRF Move.cpp OM table, meshDeviation)");
            Assert.That(compensation.File, Is.EqualTo("heightmap.csv"),
                        "G29 S0 saves the map, so move.compensation.file names it (RRF GCodes4.cpp gridProbing7, TrySaveHeightMap(DefaultHeightMapFile))");
        });
    }
}
