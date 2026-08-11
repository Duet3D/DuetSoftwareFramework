using DuetControlServer;
using DuetControlServer.Motion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.IO;
using System.Threading;
using DcsModel = DuetControlServer.Model.ObjectModel;

namespace UnitTests.Motion;

/// <summary>
/// Tests for how a height map is applied to a move
/// </summary>
/// <remarks>
/// A move is built in the coordinates the user asked for and committed in the coordinates the machine
/// went to, so the correction is added on the way down and taken back off on the way up. The taper
/// makes the correction depend on the height being corrected, which is what stops the round trip from
/// being a subtraction and an addition
/// </remarks>
[TestFixture]
public class BedCompensationTests
{
    /// <summary>A bed that is 0.2mm high in one corner and level in the rest</summary>
    private const string SampleMap = """
        RepRapFirmware height map file v2 generated at 2026-01-01 12:00
        axis0,axis1,min0,max0,min1,max1,radius,spacing0,spacing1,num0,num1
        X,Y,0.00,20.00,0.00,20.00,-1.00,10.00,10.00,3,3
          0.000,  0.000,  0.000
          0.000,  0.000,  0.000
          0.000,  0.000,  0.200
        """;

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static DcsModel NewModel()
        => new(new TestLifetime(), NullLogger<DcsModel>.Instance, Options.Create(new Settings()));

    private static BedCompensation Loaded(float taperHeight = 0.0f)
    {
        BedCompensation compensation = new(NewModel());
        Assert.That(compensation.LoadAsync(new StringReader(SampleMap), "heightmap.csv", CancellationToken.None)
                                .AsTask().GetAwaiter().GetResult(), Is.Null);
        compensation.SetTaperHeight(taperHeight);
        return compensation;
    }

    [Test]
    public void NoMapMeansNoCorrection()
    {
        // Every move goes through this, so an unmapped machine has to come out the other side
        // untouched rather than approximately untouched
        BedCompensation compensation = new(NewModel());
        Assert.Multiple(() =>
        {
            Assert.That(compensation.IsActive, Is.False);
            Assert.That(compensation.GetCorrection(10.0f, 10.0f, 5.0f), Is.Zero);
            Assert.That(compensation.GetRequestedHeight(10.0f, 10.0f, 5.0f), Is.EqualTo(5.0f));
        });
    }

    /// <summary>
    /// Tolerance for a reading taken at the far corner of the grid
    /// </summary>
    /// <remarks>
    /// The last row and column are clamped a hundredth of a millimetre inside the grid so that the
    /// interpolation always has a cell to work in, which is what RepRapFirmware does. A reading taken
    /// there is therefore a shade short of the corner's own height, by an amount that follows from
    /// the point spacing rather than from any rounding
    /// </remarks>
    private const float CornerTolerance = 1e-3f;

    [Test]
    public void TheNozzleFollowsTheBedWhenTheCorrectionIsNotTapered()
    {
        BedCompensation compensation = Loaded();
        Assert.Multiple(() =>
        {
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 0.0f), Is.EqualTo(0.2f).Within(CornerTolerance), "the high corner");
            Assert.That(compensation.GetCorrection(0.0f, 0.0f, 0.0f), Is.EqualTo(0.0f).Within(1e-4f), "the level corner");
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 50.0f), Is.EqualTo(0.2f).Within(CornerTolerance),
                        "without a taper the correction applies all the way up");
        });
    }

    [Test]
    public void AboveTheTaperHeightTheBedIsForgotten()
    {
        // This is the point of the taper: a tall print should come out square rather than following
        // the shape of the bed all the way to the top
        BedCompensation compensation = Loaded(taperHeight: 10.0f);
        Assert.Multiple(() =>
        {
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 10.0f), Is.Zero, "at the taper height");
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 20.0f), Is.Zero, "above it");
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 0.0f), Is.EqualTo(0.2f).Within(CornerTolerance),
                        "on the bed the full correction still applies");
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 5.0f), Is.EqualTo(0.1f).Within(CornerTolerance),
                        "half way up, half the correction");
        });
    }

    [Test]
    public void TheReportedHeightIsTheOneThatWasAskedFor()
    {
        // The user reads back the coordinate they commanded, not the one the machine moved to, so
        // the correction has to invert exactly - including through the taper, where it is not a
        // simple subtraction because the correction depends on the height it is correcting
        foreach (float taper in new[] { 0.0f, 10.0f })
        {
            BedCompensation compensation = Loaded(taper);
            foreach (float requested in new[] { 0.0f, 1.0f, 5.0f, 9.0f })
            {
                float commanded = requested + compensation.GetCorrection(20.0f, 20.0f, requested);
                Assert.That(compensation.GetRequestedHeight(20.0f, 20.0f, commanded),
                            Is.EqualTo(requested).Within(1e-3f), $"taper {taper}, requested {requested}");
            }
        }
    }

    [Test]
    public void ZeroingTheMapMakesTheProbedPointReadZero()
    {
        // The whole point of the shift. A G30 has just declared the nozzle to be at the trigger
        // height here, so the map must not immediately correct the machine at the very point that
        // defined its datum - it would fight the operation that zeroed it
        BedCompensation compensation = Loaded();
        Assert.That(compensation.GetCorrection(20.0f, 20.0f, 0.0f), Is.Not.Zero, "the map deviates here to begin with");

        compensation.SetZeroHeightError(20.0f, 20.0f);

        Assert.Multiple(() =>
        {
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 0.0f), Is.Zero.Within(1e-4f),
                        "the point the machine was zeroed at");
            Assert.That(compensation.GetCorrection(0.0f, 0.0f, 0.0f), Is.EqualTo(-0.2f).Within(CornerTolerance),
                        "the rest of the bed keeps its shape, measured from the new datum");
        });
    }

    [Test]
    public void TheZeroShiftInvertsWithTheCorrection()
    {
        // Both directions of the transform go through the same height computation, so a shift
        // applied to one and forgotten in the other would show up as a reported position that drifts
        // by the shift every time the machine is probed
        foreach (float taper in new[] { 0.0f, 10.0f })
        {
            BedCompensation compensation = Loaded(taper);
            compensation.SetZeroHeightError(20.0f, 20.0f);

            foreach (float requested in new[] { 0.0f, 1.0f, 5.0f, 9.0f })
            {
                float commanded = requested + compensation.GetCorrection(0.0f, 0.0f, requested);
                Assert.That(compensation.GetRequestedHeight(0.0f, 0.0f, commanded),
                            Is.EqualTo(requested).Within(1e-3f), $"taper {taper}, requested {requested}");
            }
        }
    }

    [Test]
    public void ANewMapDoesNotInheritTheOldOnesZeroPoint()
    {
        // The shift normalises one particular map at one particular point, so it means nothing once
        // that map has been replaced. RepRapFirmware clears it in SetIdentityTransform for the same
        // reason
        BedCompensation compensation = Loaded();
        compensation.SetZeroHeightError(20.0f, 20.0f);

        Assert.That(compensation.LoadAsync(new StringReader(SampleMap), "heightmap.csv", CancellationToken.None)
                                .AsTask().GetAwaiter().GetResult(), Is.Null);

        Assert.That(compensation.GetCorrection(20.0f, 20.0f, 0.0f), Is.EqualTo(0.2f).Within(CornerTolerance));
    }

    [Test]
    public void ClearingStopsTheCorrectionBeingApplied()
    {
        BedCompensation compensation = Loaded();
        compensation.ClearAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Multiple(() =>
        {
            Assert.That(compensation.IsActive, Is.False);
            Assert.That(compensation.GetCorrection(20.0f, 20.0f, 0.0f), Is.Zero);
        });
    }
}
