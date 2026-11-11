using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M201 and M201.1, the acceleration limits and the reduced acceleration</summary>
[TestFixture]
[GCode("M201", "M201.1")]
public class M201Tests : BenchFixture
{
    /// <summary>M201 sets the normal acceleration of axes and extruders, in mm/s^2</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 201 with fraction 0 and Move2.cpp Move::SetAcceleration(..., false);
    /// move.axes[].acceleration is reported through InverseConvertAcceleration in mm/s^2
    /// (Move.cpp axes table)
    /// </remarks>
    [Test]
    public async Task M201SetsAcceleration()
    {
        // TODO test setting multiple extruders
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        await bench.Host.ExecuteCodeAsync("M201 X1250 Y1100 E3000");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Acceleration), Is.EqualTo(1250.0).Within(1e-2),
                        "M201 X sets move.axes[0].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Acceleration), Is.EqualTo(1100.0).Within(1e-2),
                        "M201 Y sets move.axes[1].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Acceleration), Is.EqualTo(3000.0).Within(1e-2),
                        "M201 E sets move.extruders[0].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
        });
    }

    /// <summary>
    /// M201.1 sets the reduced acceleration used by probing and stall homing moves, stored
    /// independently of the normal acceleration
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 201 with fraction 1 writes reducedAccelerations[] without clamping
    /// (Move2.cpp SetAcceleration). RRF's report takes min(reduced, normal) (Move.h inline
    /// Acceleration); the stored value is what is asserted here, see the TODO below. The bench
    /// config runs M201 X500
    /// </remarks>
    [Test]
    public async Task M201Dot1SetsReducedAcceleration()
    {
        // TODO test setting multiple extruders
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        double acceleration = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Acceleration);
        await bench.Host.ExecuteCodeAsync("M201.1 X55");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].ReducedAcceleration).Result, Is.EqualTo(55.0).Within(1e-2),
                        "M201.1 X sets move.axes[0].reducedAcceleration (mm/s^2, RRF GCodes2.cpp case 201 frac 1)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].Acceleration).Result, Is.EqualTo(acceleration).Within(1e-2),
                        "M201.1 leaves move.axes[0].acceleration alone (RRF Move2.cpp SetAcceleration reduced branch)");
        });

        // TODO possible RRF bug where it allows reducedAcceleration to be greater than normalAcceleration
        await bench.Host.ExecuteCodeAsync("M201.1 X800");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].ReducedAcceleration), Is.EqualTo(800).Within(1e-2),
                    "M201.1 X800 stores 800 even above the normal acceleration (RRF Move2.cpp SetAcceleration does not clamp)");
    }
}
