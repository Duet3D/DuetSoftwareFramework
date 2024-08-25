using DuetAPI.Utility;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.IO;
using System.Threading.Tasks;

namespace UnitTests.Utility
{
    [TestFixture]
    public class HeightMap
    {
        [Test]
        public void Read()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "../../../Utility/heightmap.csv");

            Heightmap map = new();
            map.Load(path);

            ClassicAssert.AreEqual(30, map.XMin, 0.0001);
            ClassicAssert.AreEqual(180, map.XMax, 0.0001);
            ClassicAssert.AreEqual(30, map.XSpacing, 0.0001);
            ClassicAssert.AreEqual(30, map.YMin, 0.0001);
            ClassicAssert.AreEqual(180, map.YMax, 0.0001);
            ClassicAssert.AreEqual(30, map.YSpacing, 0.0001);
            ClassicAssert.AreEqual(-1, map.Radius, 0.0001);
            ClassicAssert.AreEqual(6, map.NumX);
            ClassicAssert.AreEqual(6, map.NumY);
            ClassicAssert.AreEqual(36, map.ZCoordinates.Length);

            ClassicAssert.AreEqual(0.088, map.ZCoordinates[0], 0.0001);
            ClassicAssert.AreEqual(0.086, map.ZCoordinates[1], 0.0001);
            // ...
            ClassicAssert.AreEqual(0.056, map.ZCoordinates[34], 0.0001);
            ClassicAssert.IsNaN(map.ZCoordinates[35]);
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

            ClassicAssert.AreEqual(30, map.XMin, 0.0001);
            ClassicAssert.AreEqual(180, map.XMax, 0.0001);
            ClassicAssert.AreEqual(30, map.XSpacing, 0.0001);
            ClassicAssert.AreEqual(30, map.YMin, 0.0001);
            ClassicAssert.AreEqual(180, map.YMax, 0.0001);
            ClassicAssert.AreEqual(30, map.YSpacing, 0.0001);
            ClassicAssert.AreEqual(-1, map.Radius, 0.0001);
            ClassicAssert.AreEqual(6, map.NumX);
            ClassicAssert.AreEqual(6, map.NumY);
            ClassicAssert.AreEqual(36, map.ZCoordinates.Length);

            for (int i = 0; i < tempMap.ZCoordinates.Length; i++)
            {
                ClassicAssert.AreEqual(tempMap.ZCoordinates[i], map.ZCoordinates[i], 0.0001);
            }
        }

        [Test]
        public async Task ReadAsync()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "../../../Utility/heightmap.csv");

            Heightmap map = new();
            await map.LoadAsync(path);

            ClassicAssert.AreEqual(30, map.XMin, 0.0001);
            ClassicAssert.AreEqual(180, map.XMax, 0.0001);
            ClassicAssert.AreEqual(30, map.XSpacing, 0.0001);
            ClassicAssert.AreEqual(30, map.YMin, 0.0001);
            ClassicAssert.AreEqual(180, map.YMax, 0.0001);
            ClassicAssert.AreEqual(30, map.YSpacing, 0.0001);
            ClassicAssert.AreEqual(-1, map.Radius, 0.0001);
            ClassicAssert.AreEqual(6, map.NumX);
            ClassicAssert.AreEqual(6, map.NumY);
            ClassicAssert.AreEqual(36, map.ZCoordinates.Length);

            ClassicAssert.AreEqual(0.088, map.ZCoordinates[0], 0.0001);
            ClassicAssert.AreEqual(0.086, map.ZCoordinates[1], 0.0001);
            // ...
            ClassicAssert.AreEqual(0.056, map.ZCoordinates[34], 0.0001);
            ClassicAssert.IsNaN(map.ZCoordinates[35]);
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

            ClassicAssert.AreEqual(30, map.XMin, 0.0001);
            ClassicAssert.AreEqual(180, map.XMax, 0.0001);
            ClassicAssert.AreEqual(30, map.XSpacing, 0.0001);
            ClassicAssert.AreEqual(30, map.YMin, 0.0001);
            ClassicAssert.AreEqual(180, map.YMax, 0.0001);
            ClassicAssert.AreEqual(30, map.YSpacing, 0.0001);
            ClassicAssert.AreEqual(-1, map.Radius, 0.0001);
            ClassicAssert.AreEqual(6, map.NumX);
            ClassicAssert.AreEqual(6, map.NumY);
            ClassicAssert.AreEqual(36, map.ZCoordinates.Length);

            for (int i = 0; i < tempMap.ZCoordinates.Length; i++)
            {
                ClassicAssert.AreEqual(tempMap.ZCoordinates[i], map.ZCoordinates[i], 0.0001);
            }
        }
    }
}
