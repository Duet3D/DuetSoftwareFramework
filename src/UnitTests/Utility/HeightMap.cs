using DuetAPI.Utility;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.IO;

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
            map.Load(path).Wait();

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
            tempMap.Load(path).Wait();
            tempMap.Save(tempFile).Wait();

            Heightmap map = new();
            map.Load(tempFile).Wait();

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
