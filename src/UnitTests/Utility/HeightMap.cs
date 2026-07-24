using DuetAPI.Utility;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;

namespace UnitTests.Utility;

[TestFixture]
public class HeightMap
{
    [Test]
    public void Read()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "../../../Utility/heightmap.csv");

        Heightmap map = new();
        map.Load(path);

        Assert.That(map.XMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.XMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.XSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.YSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.Radius, Is.EqualTo(-1).Within(0.0001));
        Assert.That(map.NumX, Is.EqualTo(6));
        Assert.That(map.NumY, Is.EqualTo(6));
        Assert.That(map.ZCoordinates.Length, Is.EqualTo(36));

        Assert.That(map.ZCoordinates[0], Is.EqualTo(0.088).Within(0.0001));
        Assert.That(map.ZCoordinates[1], Is.EqualTo(0.086).Within(0.0001));
        // ...
        Assert.That(map.ZCoordinates[34], Is.EqualTo(0.056).Within(0.0001));
        Assert.That(map.ZCoordinates[35], Is.NaN);
    }

    [Test]
    public void Write()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "../../../Utility/heightmap.csv");
        string tempFile = Path.GetTempFileName();
        TestContext.Out.WriteLine(tempFile);

        Heightmap tempMap = new();
        tempMap.Load(path);
        tempMap.Save(tempFile);

        Heightmap map = new();
        map.Load(tempFile);

        Assert.That(map.XMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.XMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.XSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.YSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.Radius, Is.EqualTo(-1).Within(0.0001));
        Assert.That(map.NumX, Is.EqualTo(6));
        Assert.That(map.NumY, Is.EqualTo(6));
        Assert.That(map.ZCoordinates.Length, Is.EqualTo(36));

        for (int i = 0; i < tempMap.ZCoordinates.Length; i++)
        {
            Assert.That(map.ZCoordinates[i], Is.EqualTo(tempMap.ZCoordinates[i]).Within(0.0001));
        }
    }

    [Test]
    public async Task ReadAsync()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "../../../Utility/heightmap.csv");

        Heightmap map = new();
        await map.LoadAsync(path);

        Assert.That(map.XMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.XMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.XSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.YSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.Radius, Is.EqualTo(-1).Within(0.0001));
        Assert.That(map.NumX, Is.EqualTo(6));
        Assert.That(map.NumY, Is.EqualTo(6));
        Assert.That(map.ZCoordinates.Length, Is.EqualTo(36));

        Assert.That(map.ZCoordinates[0], Is.EqualTo(0.088).Within(0.0001));
        Assert.That(map.ZCoordinates[1], Is.EqualTo(0.086).Within(0.0001));
        // ...
        Assert.That(map.ZCoordinates[34], Is.EqualTo(0.056).Within(0.0001));
        Assert.That(map.ZCoordinates[35], Is.NaN);
    }

    [Test]
    public async Task WriteAsync()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "../../../Utility/heightmap.csv");
        string tempFile = Path.GetTempFileName();
        TestContext.Out.WriteLine(tempFile);

        Heightmap tempMap = new();
        await tempMap.LoadAsync(path);
        await tempMap.SaveAsync(tempFile);

        Heightmap map = new();
        await map.LoadAsync(tempFile);

        Assert.That(map.XMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.XMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.XSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMin, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.YMax, Is.EqualTo(180).Within(0.0001));
        Assert.That(map.YSpacing, Is.EqualTo(30).Within(0.0001));
        Assert.That(map.Radius, Is.EqualTo(-1).Within(0.0001));
        Assert.That(map.NumX, Is.EqualTo(6));
        Assert.That(map.NumY, Is.EqualTo(6));
        Assert.That(map.ZCoordinates.Length, Is.EqualTo(36));

        for (int i = 0; i < tempMap.ZCoordinates.Length; i++)
        {
            Assert.That(map.ZCoordinates[i], Is.EqualTo(tempMap.ZCoordinates[i]).Within(0.0001));
        }
    }
}
