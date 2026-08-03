using DuetControlServer.Motion;
using NUnit.Framework;
using System;
using System.IO;

namespace UnitTests.Motion;

/// <summary>
/// Tests for the bed height map
/// </summary>
/// <remarks>
/// The file is RepRapFirmware's own <c>heightmap.csv</c>, so a machine that was mapped before this
/// migration has to reload the same map afterwards. That is what most of these check
/// </remarks>
[TestFixture]
public class HeightMapTests
{
    /// <summary>A three by three map with one point left unprobed</summary>
    private const string SampleMap = """
        RepRapFirmware height map file v2 generated at 2026-01-01 12:00, min error -0.100, max error 0.200, mean 0.050, deviation 0.100
        axis0,axis1,min0,max0,min1,max1,radius,spacing0,spacing1,num0,num1
        X,Y,0.00,20.00,0.00,20.00,-1.00,10.00,10.00,3,3
          0.000,  0.100,  0.200
          0.100,  0.150,  0.200
         -0.100,      0,  0.000
        """;

    private static HeightMap Read(string text)
    {
        Assert.That(HeightMap.TryRead(new StringReader(text), out HeightMap? map, out string? error), Is.True, error);
        return map!;
    }

    [Test]
    public void AMapReadsBackTheGridItWasMeasuredOver()
    {
        HeightMap map = Read(SampleMap);
        Assert.Multiple(() =>
        {
            Assert.That(map.Axes, Is.EqualTo(new[] { 'X', 'Y' }));
            Assert.That(map.Mins, Is.EqualTo(new[] { 0.0f, 0.0f }));
            Assert.That(map.Maxs, Is.EqualTo(new[] { 20.0f, 20.0f }));
            Assert.That(map.Spacings, Is.EqualTo(new[] { 10.0f, 10.0f }));
            Assert.That(map.Nums, Is.EqualTo(new[] { 3, 3 }));
            Assert.That(map.IsValid, Is.True);
        });
    }

    [Test]
    public void AnUnprobedPointIsNotAMeasurementOfZero()
    {
        // RepRapFirmware writes a bare 0 where it did not probe and 0.000 where it measured zero.
        // Losing that distinction would put a fabricated point into the statistics and make a partly
        // probed bed look flatter than it is
        HeightMap map = Read(SampleMap);
        Assert.That(map.MeasuredPoints, Is.EqualTo(8), "one of the nine points was never probed");
    }

    [Test]
    public void AMapSurvivesBeingWrittenAndReadBack()
    {
        HeightMap map = Read(SampleMap);
        StringWriter writer = new();
        map.Write(writer, new DateTime(2026, 1, 1, 12, 0, 0));

        HeightMap reloaded = Read(writer.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Nums, Is.EqualTo(map.Nums));
            Assert.That(reloaded.MeasuredPoints, Is.EqualTo(map.MeasuredPoints), "the unprobed point stays unprobed");
            Assert.That(reloaded.GetInterpolatedHeightError(5.0f, 5.0f),
                        Is.EqualTo(map.GetInterpolatedHeightError(5.0f, 5.0f)).Within(1e-4f));
        });
    }

    [Test]
    public void ThePointsThemselvesComeBackExactly()
    {
        // A grid point is a measurement, so interpolating at one has to return it rather than
        // something a rounding away from it
        HeightMap map = Read(SampleMap);
        Assert.Multiple(() =>
        {
            Assert.That(map.GetInterpolatedHeightError(0.0f, 0.0f), Is.EqualTo(0.0f).Within(1e-4f));
            Assert.That(map.GetInterpolatedHeightError(10.0f, 0.0f), Is.EqualTo(0.1f).Within(1e-4f));
            Assert.That(map.GetInterpolatedHeightError(0.0f, 10.0f), Is.EqualTo(0.1f).Within(1e-4f));
        });
    }

    [Test]
    public void BetweenPointsTheHeightIsInterpolated()
    {
        // Half way between (0,0) at 0.000 and (10,0) at 0.100, along a row that is flat in the other
        // direction, so the answer is the average of the two
        HeightMap map = Read(SampleMap);
        Assert.That(map.GetInterpolatedHeightError(5.0f, 0.0f), Is.EqualTo(0.05f).Within(1e-4f));
    }

    [Test]
    public void OutsideTheGridTheEdgeIsUsedRatherThanAnExtrapolation()
    {
        // A bed is measured where it can be probed. Extrapolating past the last point would move the
        // nozzle on evidence that was never collected, so RepRapFirmware clamps and so does this
        HeightMap map = Read(SampleMap);
        Assert.That(map.GetInterpolatedHeightError(-50.0f, -50.0f),
                    Is.EqualTo(map.GetInterpolatedHeightError(0.0f, 0.0f)).Within(1e-4f));
    }

    [Test]
    public void AFileFromAnotherFirmwareIsRefusedRatherThanMisread()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HeightMap.TryRead(new StringReader("something else\n"), out _, out string? header), Is.False);
            Assert.That(header, Does.Contain("header"));

            string badLabels = SampleMap.Replace("axis0,axis1,min0", "nonsense,axis1,min0");
            Assert.That(HeightMap.TryRead(new StringReader(badLabels), out _, out string? labels), Is.False);
            Assert.That(labels, Does.Contain("label"));
        });
    }

    [Test]
    public void AMapWrittenByAnOlderFirmwareStillLoads()
    {
        // The label line says which layout follows. A map saved by RepRapFirmware 3.2 is still a
        // valid map, and refusing it would lose a bed the user had already measured
        const string oldMap = """
            RepRapFirmware height map file v2 generated at 2026-01-01 12:00
            xmin,xmax,ymin,ymax,radius,xspacing,yspacing,xnum,ynum
            0.00,20.00,0.00,20.00,-1.00,10.00,10.00,3,3
              0.000,  0.100,  0.200
              0.100,  0.150,  0.200
             -0.100,  0.000,  0.000
            """;

        HeightMap map = Read(oldMap);
        Assert.Multiple(() =>
        {
            Assert.That(map.Axes, Is.EqualTo(new[] { 'X', 'Y' }), "an unnamed grid is XY");
            Assert.That(map.Nums, Is.EqualTo(new[] { 3, 3 }));
            Assert.That(map.GetInterpolatedHeightError(10.0f, 0.0f), Is.EqualTo(0.1f).Within(1e-4f));
        });
    }

    [Test]
    public void TheStatisticsDescribeOnlyThePointsThatWereProbed()
    {
        HeightMap map = Read(SampleMap);
        (float mean, float deviation, float minError, float maxError) = map.GetStatistics();
        Assert.Multiple(() =>
        {
            Assert.That(minError, Is.EqualTo(-0.1f).Within(1e-4f));
            Assert.That(maxError, Is.EqualTo(0.2f).Within(1e-4f));
            Assert.That(mean, Is.EqualTo(0.08125f).Within(1e-4f), "the mean of the eight probed points");
            Assert.That(deviation, Is.GreaterThan(0.0f));
        });
    }
}
