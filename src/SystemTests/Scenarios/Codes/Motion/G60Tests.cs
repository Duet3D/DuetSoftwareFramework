using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>G60, saving a restore point</summary>
[TestFixture]
[GCode("G60")]
public class G60Tests : BenchFixture
{
    /// <summary>
    /// G60 saves the current user position, feed rate and tool into the restore point slot S
    /// names, slot 0 by default
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp SavePosition (G60) calls MovementState::SavePosition (RawMove.cpp), which
    /// copies currentUserPosition into moveCoords, the raw machine state feed rate into
    /// originalFeedRate and the current tool number; RestorePoint.cpp reports them as coords,
    /// feedRate and toolNumber
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G60SavesRestorePoint()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        await bench.Host.ExecuteCodeAsync("G1 X12 Y7 F1800");
        await bench.Host.ExecuteCodeAsync("M400");

        string reply = await bench.Host.ExecuteCodeAsync("G60 S2");
        Assert.That(reply.Trim(), Is.Empty, "G60 S2 executes without error (RRF GCodes3.cpp SavePosition)");
#pragma warning disable CS0618
        RestorePoint restorePoint = await bench.Host.ReadModelAsync(model => model.State.RestorePoints[2]);
#pragma warning restore
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(restorePoint!.Coords[0], Is.EqualTo(12.0).Within(1e-3),
                        "G60 S2 saves X into state.restorePoints[2].coords[0] (RRF RawMove.cpp MovementState::SavePosition)");
            Assert.That(restorePoint!.Coords[1], Is.EqualTo(7.0).Within(1e-3),
                        "G60 S2 saves Y into state.restorePoints[2].coords[1] (RRF RawMove.cpp MovementState::SavePosition)");
            Assert.That(restorePoint!.FeedRate, Is.EqualTo(1800.0).Within(1e-3),
                        "G60 S2 saves the raw feed rate into state.restorePoints[2].feedRate (RRF RestorePoint.cpp originalFeedRate)");
            Assert.That(restorePoint!.ToolNumber, Is.EqualTo(-1.0),
                        "with no tool selected state.restorePoints[2].toolNumber is -1 (RRF RawMove.cpp MovementState::SavePosition)");
        });

        await bench.Host.ExecuteCodeAsync("G60");
#pragma warning disable CS0618
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.RestorePoints[0].Coords[0]), Is.EqualTo(12.0).Within(1e-3),
                    "G60 without S saves into state.restorePoints[0] (RRF GCodes3.cpp SavePosition, default S0)");
#pragma warning restore
    }
}
