using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M564, the axis limit flags</summary>
[TestFixture]
[GCode("M564")]
public class M564Tests : BenchFixture
{
    /// <summary>M564 S drives move.limitAxes and H drives move.noMovesBeforeHoming</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 564 writes limitAxes and noMovesBeforeHoming, which the object model
    /// reports as move.limitAxes and move.noMovesBeforeHoming (Move.cpp table). The bench config
    /// runs M564 H0 S0, so both start false
    /// </remarks>
    [Test]
    public async Task M564SetsLimitFlags()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.False,
                        "the bench config's M564 S0 cleared move.limitAxes");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.False,
                        "the bench config's M564 H0 cleared move.noMovesBeforeHoming");
        });

        await bench.Host.ExecuteCodeAsync("M564 S1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.True,
                        "M564 S1 sets move.limitAxes (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.False,
                        "M564 S1 leaves move.noMovesBeforeHoming alone, H was not given (RRF GCodes2.cpp case 564)");
        });

        await bench.Host.ExecuteCodeAsync("M564 H1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.True,
                        "M564 H1 leaves move.limitAxes alone, S was not given (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.True,
                        "M564 H1 sets move.noMovesBeforeHoming (RRF GCodes2.cpp case 564)");
        });

        await bench.Host.ExecuteCodeAsync("M564 S0 H0");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.False,
                        "M564 S0 clears move.limitAxes (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.False,
                        "M564 H0 clears move.noMovesBeforeHoming (RRF GCodes2.cpp case 564)");
        });
    }
}
