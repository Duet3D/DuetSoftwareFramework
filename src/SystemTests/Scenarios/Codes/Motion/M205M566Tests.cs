using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M205 and M566, jerk</summary>
[TestFixture]
[GCode("M205", "M566")]
public class M205M566Tests : BenchFixture
{
    /// <summary>
    /// M566 (mm/min) sets the machine jerk limit and pulls the printing jerk with it; M205 (mm/s)
    /// sets only the printing jerk and is clamped to the machine limit
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::SetInstantDv: includingMax (M566) writes both arrays, otherwise (M205)
    /// printing = min(value, max). move.axes[].jerk and .printingJerk are reported in mm/min
    /// (Move.cpp axes table, InverseConvertSpeedToMmPerMin)
    /// </remarks>
    [Test]
    public async Task M205AndM566SetJerk()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Axis axis = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        await bench.Host.ExecuteCodeAsync("M566 X1200 E300");
        Assert.Multiple(() =>
        {
            Assert.That(axis.Jerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].jerk (mm/min, RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(axis.PrintingJerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].printingJerk as well (RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(extruder.Jerk, Is.EqualTo(300.0).Within(1e-2),
                        "M566 E sets move.extruders[0].jerk (mm/min, RRF Move2.cpp SetInstantDv)");
        });

        await bench.Host.ExecuteCodeAsync("M205 X5");
        Assert.Multiple(() =>
        {
            Assert.That(axis.PrintingJerk, Is.EqualTo(300.0).Within(1e-2),
                        "M205 X5 is 5 mm/s, so move.axes[0].printingJerk becomes 300 mm/min (RRF GCodes2.cpp case 205)");
            Assert.That(axis.Jerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M205 leaves the machine limit move.axes[0].jerk alone (RRF Move2.cpp SetInstantDv, not includingMax)");
        });
    }
}
